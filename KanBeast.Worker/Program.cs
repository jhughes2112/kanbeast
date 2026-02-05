using System.Text.Json;
using CommandLine;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.Extensions.Logging;

return await Program.Main(args);

public partial class Program
{
    public static async Task<int> Main(string[] args)
    {
        using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });

        ILogger<Program> logger = loggerFactory.CreateLogger<Program>();
        logger.LogInformation("KanBeast Worker Starting...");

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
            logger.LogInformation("Usage: KanBeast.Worker --ticket-id <id> --server-url <url>");
            logger.LogInformation("Sleeping 100 seconds before exit to allow log inspection...");
            await Task.Delay(TimeSpan.FromSeconds(100));
            return 1;
        }

        KanbanApiClient apiClient = new KanbanApiClient(options.ServerUrl);

        try
        {
            WorkerConfig config = BuildConfiguration(options);
            logger.LogInformation("Worker initialized for ticket: {TicketId}", config.TicketId);

            GitService gitService = new GitService(config.GitConfig);
            ICompaction managerCompaction = BuildCompaction(config.ManagerCompaction, config.LLMConfigs, config.GetPrompt("manager-compaction")); 
			ICompaction developerCompaction = BuildCompaction(config.DeveloperCompaction, config.LLMConfigs, config.GetPrompt("developer-compaction"));

            string logDirectory = Path.Combine(Environment.CurrentDirectory, "logs");

            LlmProxy managerProxy = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds, managerCompaction, logDirectory, $"tik-{config.TicketId}-mgr", config.JsonLogging);
            LlmProxy developerProxy = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds, developerCompaction, logDirectory, $"tik-{config.TicketId}-dev", config.JsonLogging);

            LlmProxy managerLlmService = managerProxy;
            LlmProxy developerLlmService = developerProxy;

            logger.LogInformation("Fetching ticket details...");
            TicketDto? ticket = await apiClient.GetTicketAsync(config.TicketId);

            if (ticket != null)
            {
                logger.LogInformation("Ticket: {Title}", ticket.Title);
                await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Initialized and starting work");

                string workDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "repo"));
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, true);
                }
                Directory.CreateDirectory(workDir);
                logger.LogInformation("Working directory: {WorkDir}", workDir);

                if (!string.IsNullOrEmpty(config.GitConfig.RepositoryUrl))
                {
                    logger.LogInformation("Cloning repository...");
                    await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Cloning repository");
                    await gitService.CloneRepositoryAsync(config.GitConfig.RepositoryUrl, workDir);
                    await gitService.ConfigureGitAsync(config.GitConfig.Username, config.GitConfig.Email, workDir);

                    string branchName = ticket.BranchName ?? $"feature/ticket-{ticket.Id}";
                    logger.LogInformation("Branch: {BranchName}", branchName);
                    await gitService.CreateOrCheckoutBranchAsync(branchName, workDir);

                    if (string.IsNullOrEmpty(ticket.BranchName))
                    {
                        await apiClient.SetBranchNameAsync(ticket.Id, branchName);
                    }

                    AgentOrchestrator orchestrator = new AgentOrchestrator(loggerFactory.CreateLogger<AgentOrchestrator>(), apiClient, managerLlmService, developerLlmService, config.GetPrompt("manager-system"), config.GetPrompt("developer"), config.MaxIterationsPerSubtask);
                    logger.LogInformation("Starting agent orchestrator...");
                    await orchestrator.RunAsync(ticket, workDir, CancellationToken.None);

                    logger.LogInformation("Worker completed");
                    return 0;
                }
                else
                {
                    logger.LogError("No repository URL configured");
                    await apiClient.AddActivityLogAsync(ticket.Id, "Worker: No repository URL configured");
                    await MoveTicketToBacklogAsync(logger, apiClient, ticket.Id);
                    return 1;
                }
            }
            else
            {
                logger.LogError("Ticket {TicketId} not found", config.TicketId);
                return 1;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Worker failed: {Message}", ex.Message);

            try
            {
                await apiClient.AddActivityLogAsync(options.TicketId, $"Worker: Failed with error - {ex.Message}");
                await MoveTicketToBacklogAsync(logger, apiClient, options.TicketId);
            }
            catch (Exception reportException)
            {
                logger.LogError(reportException, "Failed to report error to server: {Message}", reportException.Message);
            }

            logger.LogInformation("Sleeping 100 seconds before exit to allow log inspection...");
            await Task.Delay(TimeSpan.FromSeconds(100));
            return 1;
        }
    }

    private static async Task MoveTicketToBacklogAsync(ILogger<Program> logger, KanbanApiClient apiClient, string ticketId)
    {
        try
        {
            TicketDto? updated = await apiClient.UpdateTicketStatusAsync(ticketId, "Backlog");
            if (updated != null)
            {
                logger.LogInformation("Ticket moved back to Backlog after worker failure");
                await apiClient.AddActivityLogAsync(ticketId, "Worker: Moved ticket to Backlog due to failure");
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

        Dictionary<string, string> prompts = new Dictionary<string, string>
        {
            ["manager-system"] = LoadPromptFromDisk(resolvedPromptDirectory, "manager-system"),
            ["developer"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer"),
            ["manager-compaction"] = LoadPromptFromDisk(resolvedPromptDirectory, "manager-compaction-summary"),
            ["developer-compaction"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer-compaction-summary")
        };

        WorkerConfig config = new WorkerConfig
        {
            TicketId = ticketId,
            ServerUrl = serverUrl,
            GitConfig = settings.GitConfig,
            LLMConfigs = settings.LLMConfigs,
            LlmRetryCount = settings.LlmRetryCount,
            LlmRetryDelaySeconds = settings.LlmRetryDelaySeconds,
            ManagerCompaction = settings.ManagerCompaction,
            DeveloperCompaction = settings.DeveloperCompaction,
            Prompts = prompts,
            PromptDirectory = resolvedPromptDirectory,
            JsonLogging = options.JsonLogging
        };

        return config;
    }

    private static string LoadPromptFromDisk(string promptDirectory, string promptName)
    {
        string filePath = Path.Combine(promptDirectory, $"{promptName}.txt");
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"Error: Required prompt file not found: {filePath}");
            throw new FileNotFoundException($"Required prompt file not found: {filePath}", filePath);
        }

        return File.ReadAllText(filePath);
    }

    private static WorkerSettings LoadWorkerSettings()
{
    string resolvedPath = ResolveSettingsPath();

    if (!File.Exists(resolvedPath))
    {
        Console.WriteLine($"Error: Required settings file not found: {resolvedPath}");
        throw new FileNotFoundException($"Required settings file not found: {resolvedPath}", resolvedPath);
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

        private static string ResolveSettingsPath()
        {
            string resolvedPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "settings.json"));

            return resolvedPath;
        }

        private static string ResolvePromptDirectory()
        {
            return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "prompts"));
        }

        private static ICompaction BuildCompaction(CompactionSettings settings, List<LLMConfig> llmConfigs, string compactionPrompt)
        {
            ICompaction compaction = new CompactionNone();

            if (string.Equals(settings.Type, "summarize", StringComparison.OrdinalIgnoreCase) && llmConfigs.Count > 0)
            {
                LLMConfig currentLlm = llmConfigs[0];
                compaction = new CompactionSummarizer(
                    compactionPrompt,
                    settings.ContextSizeThreshold);
            }

            return compaction;
        }
    }

    public class WorkerOptions
{
    [Option("ticket-id", Required = true, HelpText = "Ticket id for the worker.")]
    public required string TicketId { get; set; }

    [Option("server-url", Required = true, HelpText = "Server URL for the worker.")]
    public required string ServerUrl { get; set; }

    [Option("json", Required = false, Default = false, HelpText = "Output logs in JSON format instead of friendly format.")]
    public bool JsonLogging { get; set; }
}
