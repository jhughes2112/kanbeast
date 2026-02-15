using System.Runtime.InteropServices;
using System.Text.Json;
using CommandLine;
using KanBeast.Shared;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Tests;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker;

public class Program
{
	public static async Task<int> Main(string[] args)
	{
		if (args.Length > 0 && args[0] == "--test")
		{
			return TestRunner.RunAll();
		}

		using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
		{
			builder.AddConsole();
			builder.SetMinimumLevel(LogLevel.Information);
		});

		ILogger<Program> logger = loggerFactory.CreateLogger<Program>();
		logger.LogInformation("KanBeast Worker Starting...");

		using CancellationTokenSource cts = new CancellationTokenSource();

		Console.CancelKeyPress += (sender, e) =>
		{
			e.Cancel = true;
			logger.LogWarning("Shutdown requested via Ctrl+C");
			cts.Cancel();
		};

		WorkerOptions? options = null;
		try
		{
			options = Parser.Default.ParseArguments<WorkerOptions>(args)
				.MapResult(
					opt => opt,
					errors =>
					{
						logger.LogError("Failed to parse command line arguments");
						throw new InvalidOperationException("Failed to parse command line arguments.");
					});
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Command line parsing error: {Message}", ex.Message);
			logger.LogInformation("Usage: KanBeast.Worker --ticket-id <id> --server-url <url> [--repo <path>]");
			logger.LogInformation("Sleeping 100 seconds before exit to allow log inspection...");
			await Task.Delay(TimeSpan.FromSeconds(100));
			return 1;
		}

		KanbanApiClient apiClient = new KanbanApiClient(options.ServerUrl);
		WorkerHubClient hubClient = new WorkerHubClient(options.ServerUrl, options.TicketId);

		try
		{
			await hubClient.ConnectAsync(cts.Token);
			logger.LogInformation("Connected to server hub for ticket {TicketId}", options.TicketId);

			WorkerConfig config = BuildConfiguration(options);
			string repoDir = ResolveRepoPath(options.RepoPath);

			await RunAsync(logger, apiClient, config, repoDir, hubClient, cts.Token);

			logger.LogInformation("Worker exiting cleanly");
			return 0;
		}
		catch (OperationCanceledException)
		{
			logger.LogInformation("Worker cancelled, shutting down");
			return 0;
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Worker failed: {Message}", ex.Message);
			await apiClient.AddActivityLogAsync(options.TicketId, $"Worker: Failed with error - {ex.Message}", cts.Token);
			return 1;
		}
		finally
		{
			await hubClient.DisposeAsync();
		}
	}

	private static async Task RunAsync(
		ILogger<Program> logger,
		KanbanApiClient apiClient,
		WorkerConfig config,
		string repoDir,
		WorkerHubClient hubClient,
		CancellationToken cancellationToken)
	{
		// Wait for the ticket to exist.
		Ticket ticket;
		for (;;)
		{
			Ticket? fetched = await apiClient.GetTicketAsync(config.TicketId, cancellationToken);
			if (fetched != null)
			{
				ticket = fetched;
				break;
			}
			logger.LogWarning("Ticket {TicketId} not found, waiting...", config.TicketId);
			await hubClient.WaitForTicketChangeAsync(cancellationToken);
		}

		logger.LogInformation("Ticket found: {Title}", ticket.Title);
		await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Container started, standing by", cancellationToken);

		// One-time git setup.
		GitService gitService = new GitService(config.Settings.GitConfig);
		if (string.IsNullOrEmpty(config.Settings.GitConfig.RepositoryUrl))
		{
			throw new InvalidOperationException("No repository URL configured");
		}

		if (Directory.Exists(repoDir))
		{
			ForceDeleteDirectory(repoDir);
		}
		Directory.CreateDirectory(repoDir);

		logger.LogInformation("Cloning repository...");
		await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Cloning repository", cancellationToken);
		await gitService.CloneRepositoryAsync(config.Settings.GitConfig.RepositoryUrl, repoDir);
		await gitService.ConfigureGitAsync(config.Settings.GitConfig.Username, config.Settings.GitConfig.Email, repoDir);

		string branchName = ticket.BranchName ?? $"feature/ticket-{ticket.Id}";
		logger.LogInformation("Branch: {BranchName}", branchName);
		await gitService.CreateOrCheckoutBranchAsync(branchName, repoDir);
		if (string.IsNullOrEmpty(ticket.BranchName))
		{
			await apiClient.SetBranchNameAsync(ticket.Id, branchName, cancellationToken);
		}

		// One-time session setup.
		string dateNow = DateTime.Now.ToString();
		Dictionary<string, string> prompts = new Dictionary<string, string>
		{
			{ "planning", config.GetPrompt("planning").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "developer", config.GetPrompt("developer").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "subagent", config.GetPrompt("subagent").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "compaction", config.GetPrompt("compaction").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) }
		};

		TicketHolder ticketHolder = new TicketHolder(ticket);
		LlmProxy llmProxy = new LlmProxy(config.Settings.LLMConfigs);

		AgentOrchestrator orchestrator = new AgentOrchestrator(
			LoggerFactory.Create(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); }).CreateLogger<AgentOrchestrator>(),
			config.Settings.Compaction,
			config.Settings.LLMConfigs);

		WorkerSession.Start(apiClient, llmProxy, prompts, ticketHolder, repoDir, cancellationToken, hubClient, config.Settings.WebSearch);

		// Create or reconstitute the planning conversation immediately so the user
		// can see it in the chat dropdown even before the ticket goes Active.
		await orchestrator.EnsurePlanningConversationAsync(ticketHolder, cancellationToken);
		logger.LogInformation("Planning conversation ready, entering reactive loop");

		try
		{
			await orchestrator.RunReactiveLoopAsync(ticketHolder, cancellationToken);
		}
		finally
		{
			WorkerSession.Stop();
		}
	}

	private static WorkerConfig BuildConfiguration(WorkerOptions options)
	{
		string ticketId = options.TicketId;
		string serverUrl = options.ServerUrl;

		SettingsFile settings = LoadWorkerSettings();

		string resolvedPromptDirectory = ResolvePromptDirectory();

		if (!Directory.Exists(resolvedPromptDirectory))
		{
			Console.WriteLine($"Error: Prompt directory not found: {resolvedPromptDirectory}");
			throw new DirectoryNotFoundException($"Prompt directory not found: {resolvedPromptDirectory}");
		}

		string[] requiredPrompts = new string[] { "planning", "developer", "subagent", "compaction" };

		Dictionary<string, string> prompts = new Dictionary<string, string>();
		string[] promptFiles = Directory.GetFiles(resolvedPromptDirectory, "*.txt");

		foreach (string filePath in promptFiles)
		{
			string key = Path.GetFileNameWithoutExtension(filePath);
			string content = File.ReadAllText(filePath);
			prompts[key] = content;
			Console.WriteLine($"Loaded prompt: {key}");
		}

		foreach (string required in requiredPrompts)
		{
			if (!prompts.ContainsKey(required))
			{
				Console.WriteLine($"Error: Required prompt file not found: {required}.txt");
				throw new FileNotFoundException($"Required prompt file not found: {required}.txt", Path.Combine(resolvedPromptDirectory, $"{required}.txt"));
			}
		}

		WorkerConfig config = new WorkerConfig
		{
			TicketId = ticketId,
			ServerUrl = serverUrl,
			Settings = settings,
			Prompts = prompts,
			PromptDirectory = resolvedPromptDirectory
		};

		return config;
	}

	private static SettingsFile LoadWorkerSettings()
	{
		string resolvedPath = ResolveSettingsPath();

		if (!File.Exists(resolvedPath))
		{
			Console.WriteLine($"Settings file not found at {resolvedPath}, creating default settings...");
			SettingsFile defaultSettings = new SettingsFile();
			SaveWorkerSettings(defaultSettings, resolvedPath);
			Console.WriteLine("Default settings file created. Please configure LLM and Git settings.");
			throw new InvalidOperationException($"Default settings file created at {resolvedPath}. Please configure LLM and Git settings before running.");
		}

		string json = File.ReadAllText(resolvedPath);
		JsonSerializerOptions options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		};

		SettingsFile? settings = JsonSerializer.Deserialize<SettingsFile>(json, options);

		if (settings == null)
		{
			Console.WriteLine($"Error: Failed to deserialize settings from: {resolvedPath}");
			throw new InvalidOperationException($"Failed to deserialize settings from: {resolvedPath}");
		}

		if (settings.LLMConfigs.Count == 0)
		{
			Console.WriteLine("Error: No LLM configurations found in settings");
			throw new InvalidOperationException("No LLM configurations found in settings. At least one LLM config is required.");
		}

		return settings;
	}

	private static void SaveWorkerSettings(SettingsFile settings, string path)
	{
		JsonSerializerOptions options = new JsonSerializerOptions
		{
			WriteIndented = true,
			PropertyNamingPolicy = JsonNamingPolicy.CamelCase
		};

		string json = JsonSerializer.Serialize(settings, options);
		File.WriteAllText(path, json);
	}

	private static string ResolveSettingsPath()
	{
		string resolvedPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "settings.json"));

		return resolvedPath;
	}

	private static string ResolvePromptDirectory()
	{
		return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "prompts"));
	}

	// Resolves a repo path that may be relative to the CWD.
	// On Windows, converts to WSL format /mnt/driveletter/... for Docker/WSL compatibility.
	private static string ResolveRepoPath(string repoPath)
	{
		if (!Path.IsPathFullyQualified(repoPath))
		{
			repoPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, repoPath));
		}

		if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && repoPath.Length >= 2 && repoPath[1] == ':')
		{
			char drive = char.ToLowerInvariant(repoPath[0]);
			string rest = repoPath.Substring(2).Replace('\\', '/');
			return $"/mnt/{drive}{rest}";
		}

		return repoPath;
	}

	// Git marks pack files as read-only, which causes Directory.Delete to throw on Windows.
	private static void ForceDeleteDirectory(string path)
	{
		foreach (string file in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

		Directory.Delete(path, true);
	}
}

public class WorkerOptions
{
	[Option("ticket-id", Required = true, HelpText = "Ticket id for the worker.")]
	public required string TicketId { get; set; }

	[Option("server-url", Required = true, HelpText = "Server URL for the worker.")]
	public required string ServerUrl { get; set; }

	[Option("repo", Required = false, Default = "/repo", HelpText = "Path where the git repository will be cloned.")]
	public string RepoPath { get; set; } = string.Empty;
}
