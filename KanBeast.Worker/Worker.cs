using System.Text.Json;
using KanBeast.Shared;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Tests;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.IO;

namespace KanBeast.Worker;

public static class Worker
{
	public static async Task<int> RunAsync(string ticketId, string serverUrl, string repoPath, ILogger logger, CancellationToken cancellationToken)
	{
		logger.LogInformation("KanBeast Worker Starting...");

		KanbanApiClient apiClient = new KanbanApiClient(serverUrl);
		WorkerHubClient hubClient = new WorkerHubClient(serverUrl, ticketId);

		try
		{
			await hubClient.ConnectAsync(cancellationToken);
			logger.LogInformation("Connected to server hub for ticket {TicketId}", ticketId);

			SettingsFile settings = LoadWorkerSettings();

			string resolvedPromptDirectory = ResolvePromptDirectory();

			if (!Directory.Exists(resolvedPromptDirectory))
			{
				Console.WriteLine($"Error: Prompt directory not found: {resolvedPromptDirectory}");
				throw new DirectoryNotFoundException($"Prompt directory not found: {resolvedPromptDirectory}");
			}

			string[] requiredPrompts = new string[] { "planning", "planning-active", "developer", "subagent-dev", "subagent-planning", "compaction" };

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

           // Make provider credentials available to tests so they can decide to
			// run or skip integration tests that depend on settings.
			WorkerSession.UpdateProviderCredentials(config.Settings.Endpoint, config.Settings.ApiKey);

			// Always run unit tests on startup to ensure changes are exercised.
			int startupTestExit = TestRunner.RunAll(config);
			if (startupTestExit != 0)
			{
				Console.WriteLine($"Tests failed.  Aborting.");
				return startupTestExit;
			}

			string repoDir = ResolveRepoPath(repoPath);

			await RunAsync(logger, apiClient, config, repoDir, hubClient, cancellationToken);

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
			await apiClient.AddActivityLogAsync(ticketId, $"Worker: Failed with error - {ex.Message}", cancellationToken);
			return 1;
		}
		finally
		{
			await hubClient.DisposeAsync();
		}
	}

 private static async Task RunAsync(
		ILogger logger,
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
		Dictionary<string, string> prompts = new Dictionary<string, string>();
		foreach ((string key, string raw) in config.Prompts)
		{
			prompts[key] = raw
				.Replace("{repoDir}", repoDir)
				.Replace("{currentDate}", dateNow)
				.Replace("{ticketId}", config.TicketId);
		}

		TicketHolder ticketHolder = new TicketHolder(ticket);
		LlmRegistry llmProxy = new LlmRegistry(config.Settings.Endpoint, config.Settings.ApiKey, config.Settings.LLMConfigs);

		AgentOrchestrator orchestrator = new AgentOrchestrator(
			LoggerFactory.Create(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); }).CreateLogger<AgentOrchestrator>(),
			config.Settings.LLMConfigs);

		WorkerSession.Start(apiClient, llmProxy, prompts, ticketHolder, repoDir, cancellationToken, hubClient, config.Settings.Endpoint, config.Settings.ApiKey, config.Settings.WebSearch, config.Settings.Compaction);

		try
		{
			await orchestrator.RunAsync(ticketHolder, cancellationToken);
		}
		finally
		{
			WorkerSession.Stop();
		}
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
	// Normalizes to forward slashes so LLMs can reason about the path without
	// backslash escaping issues. Windows APIs accept forward slashes natively.
	private static string ResolveRepoPath(string repoPath)
	{
		if (!Path.IsPathFullyQualified(repoPath))
		{
			repoPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, repoPath));
		}

		return repoPath.Replace('\\', '/');
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

