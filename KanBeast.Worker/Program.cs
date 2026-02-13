using System.Text.Json;
using CommandLine;
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

		// Connect to server's SignalR hub for chat relay.
		WorkerHubClient? hubClient = null;
		try
		{
			hubClient = new WorkerHubClient(options.ServerUrl, options.TicketId);
			await hubClient.ConnectAsync(cts.Token);
			logger.LogInformation("Connected to server hub for ticket {TicketId}", options.TicketId);
		}
		catch (Exception hubEx)
		{
			logger.LogWarning(hubEx, "Failed to connect to server hub: {Message}", hubEx.Message);
			if (hubClient != null)
			{
				await hubClient.DisposeAsync();
			}
			hubClient = null;
		}

		try
		{
			WorkerConfig config = BuildConfiguration(options);
			logger.LogInformation("Worker initialized for ticket: {TicketId}", config.TicketId);

			string repoDir = Path.GetFullPath(options.RepoPath);

			await apiClient.AddActivityLogAsync(config.TicketId, "Worker: Container started, standing by", cts.Token);

			// Long-lived loop: poll ticket status, work when Active, idle otherwise.
			await RunWorkerLoopAsync(logger, apiClient, config, repoDir, hubClient, cts.Token);

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

			try
			{
				await apiClient.AddActivityLogAsync(options.TicketId, $"Worker: Failed with error - {ex.Message}", cts.Token);
			}
			catch (Exception reportException)
			{
				logger.LogError(reportException, "Failed to report error to server: {Message}", reportException.Message);
			}

			logger.LogInformation("Sleeping 100 seconds before exit to allow log inspection...");
			await Task.Delay(TimeSpan.FromSeconds(100));
			return 1;
		}
		finally
		{
			if (hubClient != null)
			{
				await hubClient.DisposeAsync();
			}
		}
	}

	private static async Task RunWorkerLoopAsync(
		ILogger<Program> logger,
		KanbanApiClient apiClient,
		WorkerConfig config,
		string repoDir,
		WorkerHubClient? hubClient,
		CancellationToken cancellationToken)
	{
		bool hasWorkedBefore = false;

		while (!cancellationToken.IsCancellationRequested)
		{
			TicketDto? ticket = await apiClient.GetTicketAsync(config.TicketId, cancellationToken);
			if (ticket == null)
			{
				logger.LogWarning("Ticket {TicketId} not found, waiting...", config.TicketId);
				await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
				continue;
			}

			if (ticket.Status != "Active")
			{
				// Idle — wait and poll again.
				await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
				continue;
			}

			// Ticket is Active — do work.
			logger.LogInformation("Ticket is Active, starting work: {Title}", ticket.Title);
			await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Initialized and starting work", cancellationToken);

			hasWorkedBefore = await DoWorkAsync(logger, apiClient, config, ticket, repoDir, hubClient, hasWorkedBefore, cancellationToken);
		}
	}

	private static async Task<bool> DoWorkAsync(
		ILogger<Program> logger,
		KanbanApiClient apiClient,
		WorkerConfig config,
		TicketDto ticket,
		string repoDir,
		WorkerHubClient? hubClient,
		bool hasWorkedBefore,
		CancellationToken cancellationToken)
	{
		string dateNow = DateTime.Now.ToString();

		Dictionary<string, string> prompts = new Dictionary<string, string>
		{
			{ "planning", config.GetPrompt("planning").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "qualityassurance", config.GetPrompt("qualityassurance").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "developer", config.GetPrompt("developer").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "subagent", config.GetPrompt("subagent").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) },
			{ "compaction", config.GetPrompt("compaction").Replace("{repoDir}", repoDir).Replace("{currentDate}", dateNow).Replace("{ticketId}", config.TicketId) }
		};

		GitService gitService = new GitService(config.GitConfig);
		TicketHolder ticketHolder = new TicketHolder(ticket);
		LlmProxy llmProxy = new LlmProxy(config.LLMConfigs);

		if (string.IsNullOrEmpty(config.GitConfig.RepositoryUrl))
		{
			logger.LogError("No repository URL configured");
			await apiClient.AddActivityLogAsync(ticket.Id, "Worker: No repository URL configured", cancellationToken);
			await MoveTicketToBacklogAsync(logger, apiClient, ticket.Id, cancellationToken);
			return hasWorkedBefore;
		}

		// Clone fresh on first run, re-use on subsequent runs.
		if (!hasWorkedBefore || !Directory.Exists(repoDir) || !Directory.Exists(Path.Combine(repoDir, ".git")))
		{
			if (Directory.Exists(repoDir))
			{
				Directory.Delete(repoDir, true);
			}
			Directory.CreateDirectory(repoDir);

			logger.LogInformation("Cloning repository...");
			await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Cloning repository", cancellationToken);
			await gitService.CloneRepositoryAsync(config.GitConfig.RepositoryUrl, repoDir);
			await gitService.ConfigureGitAsync(config.GitConfig.Username, config.GitConfig.Email, repoDir);
		}

		logger.LogInformation("Working directory: {WorkDir}", repoDir);

		string branchName = ticket.BranchName ?? $"feature/ticket-{ticket.Id}";
		logger.LogInformation("Branch: {BranchName}", branchName);
		await gitService.CreateOrCheckoutBranchAsync(branchName, repoDir);

		if (string.IsNullOrEmpty(ticket.BranchName))
		{
			await apiClient.SetBranchNameAsync(ticket.Id, branchName, cancellationToken);
		}

		AgentOrchestrator orchestrator = new AgentOrchestrator(
			LoggerFactory.Create(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); }).CreateLogger<AgentOrchestrator>(),
			config.Compaction,
			config.LLMConfigs);

		WorkerSession.Start(apiClient, llmProxy, prompts, ticketHolder, repoDir, cancellationToken, hubClient);
		logger.LogInformation("Starting agent orchestrator...");

		try
		{
			await orchestrator.StartAgents(ticketHolder, repoDir, cancellationToken);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Orchestrator failed: {Message}", ex.Message);
			await apiClient.AddActivityLogAsync(ticket.Id, $"Worker: Orchestrator failed - {ex.Message}", cancellationToken);
		}
		finally
		{
			WorkerSession.Stop();

			bool pushed = await gitService.CommitAndPushAsync(repoDir, $"[KanBeast] Work on ticket {ticket.Id}");
			if (pushed)
			{
				logger.LogInformation("Changes committed and pushed to feature branch");
			}
		}

		logger.LogInformation("Work cycle completed, standing by");
		return true;
	}

	private static async Task MoveTicketToBacklogAsync(ILogger<Program> logger, KanbanApiClient apiClient, string ticketId, CancellationToken cancellationToken)
	{
		try
		{
			TicketDto? updated = await apiClient.UpdateTicketStatusAsync(ticketId, "Backlog", cancellationToken);
			if (updated != null)
			{
				logger.LogInformation("Ticket moved back to Backlog after worker failure");
				await apiClient.AddActivityLogAsync(ticketId, "Worker: Moved ticket to Backlog due to failure", cancellationToken);
			}
			else
			{
				logger.LogWarning("Failed to move ticket back to Backlog - API returned null");
			}
		}
		catch (Exception cleanupException)
		{
			logger.LogError(cleanupException, "Failed to move ticket to Backlog: {Message}", cleanupException.Message);
		}
	}

	private static WorkerConfig BuildConfiguration(WorkerOptions options)
	{
		string ticketId = options.TicketId;
		string serverUrl = options.ServerUrl;

		WorkerSettings settings = LoadWorkerSettings();

		string resolvedPromptDirectory = ResolvePromptDirectory();

		if (!Directory.Exists(resolvedPromptDirectory))
		{
			Console.WriteLine($"Error: Prompt directory not found: {resolvedPromptDirectory}");
			throw new DirectoryNotFoundException($"Prompt directory not found: {resolvedPromptDirectory}");
		}

		string[] requiredPrompts = new string[] { "planning", "qualityassurance", "developer", "subagent", "compaction" };

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
			GitConfig = settings.GitConfig,
			LLMConfigs = settings.LLMConfigs,
			Compaction = settings.Compaction,
			Prompts = prompts,
			PromptDirectory = resolvedPromptDirectory
		};

		return config;
	}

	private static WorkerSettings LoadWorkerSettings()
	{
		string resolvedPath = ResolveSettingsPath();

		if (!File.Exists(resolvedPath))
		{
			Console.WriteLine($"Settings file not found at {resolvedPath}, creating default settings...");
			WorkerSettings defaultSettings = new WorkerSettings();
			SaveWorkerSettings(defaultSettings, resolvedPath);
			Console.WriteLine("Default settings file created. Please configure LLM and Git settings.");
			throw new InvalidOperationException($"Default settings file created at {resolvedPath}. Please configure LLM and Git settings before running.");
		}

		string json = File.ReadAllText(resolvedPath);
		JsonSerializerOptions options = new JsonSerializerOptions
		{
			PropertyNameCaseInsensitive = true
		};

		WorkerSettings? settings = JsonSerializer.Deserialize<WorkerSettings>(json, options);

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

	private static void SaveWorkerSettings(WorkerSettings settings, string path)
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
