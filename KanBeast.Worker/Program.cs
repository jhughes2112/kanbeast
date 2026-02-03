using System.Text.Json;
using CommandLine;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

ILogger<Program> logger = loggerFactory.CreateLogger<Program>();
logger.LogInformation("KanBeast Worker Starting...");

WorkerConfig? config = null;
try
{
    config = Parser.Default.ParseArguments<WorkerOptions>(args)
        .MapResult(
            BuildConfiguration,
            errors =>
            {
                logger.LogError("Failed to parse command line arguments");
                throw new InvalidOperationException("Failed to parse command line arguments.");
            });
}
catch (Exception ex)
{
    logger.LogError(ex, "Configuration error: {Message}", ex.Message);
    logger.LogInformation("Usage: KanBeast.Worker --ticket-id <id> --server-url <url>");
    logger.LogInformation("Sleeping 100 seconds before exit to allow log inspection...");
    await Task.Delay(TimeSpan.FromSeconds(100));
    return 1;
}

logger.LogInformation("Worker initialized for ticket: {TicketId}", config.TicketId);

try
{
    KanbanApiClient apiClient = new KanbanApiClient(config.ServerUrl);
    GitService gitService = new GitService(config.GitConfig);
    ToolExecutor toolExecutor = new ToolExecutor();
    HashSet<int> downedLlmIndices = new HashSet<int>();
    ICompaction managerCompaction = BuildCompaction(
        config.ManagerCompaction,
        config.LLMConfigs,
        config.GetPrompt("manager-compaction-summary"),
        config.GetPrompt("manager-compaction-system"));
    ICompaction developerCompaction = BuildCompaction(
        config.DeveloperCompaction,
        config.LLMConfigs,
        config.GetPrompt("developer-compaction-summary"),
        config.GetPrompt("developer-compaction-system"));
    ILlmService managerLlmService = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds, downedLlmIndices, managerCompaction);
    ILlmService developerLlmService = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds, downedLlmIndices, developerCompaction);

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

            AgentOrchestrator orchestrator = new AgentOrchestrator(
                loggerFactory.CreateLogger<AgentOrchestrator>(),
                apiClient,
                managerLlmService,
                developerLlmService,
                toolExecutor,
                config.GetPrompt("manager-system"),
                config.GetPrompt("developer-implementation"),
                config.MaxIterationsPerSubtask);

            logger.LogInformation("Starting agent orchestrator...");
            await orchestrator.RunAsync(ticket, workDir, CancellationToken.None);

            logger.LogInformation("Worker completed");
            return 0;
        }
        else
        {
            logger.LogError("No repository URL configured");
            await apiClient.AddActivityLogAsync(ticket.Id, "Worker: No repository URL configured");
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
    logger.LogInformation("Sleeping 100 seconds before exit to allow log inspection...");
    await Task.Delay(TimeSpan.FromSeconds(100));
    return 1;
}

static WorkerConfig BuildConfiguration(WorkerOptions options)
{
    string ticketId = options.TicketId;
    string serverUrl = options.ServerUrl;

    WorkerSettings settings = LoadWorkerSettings();

    string promptDirectory = "env/prompts";
    string resolvedPromptDirectory = ResolvePromptDirectory(promptDirectory);

    Dictionary<string, string> prompts = new Dictionary<string, string>
    {
        ["manager-system"] = LoadPromptFromDisk(resolvedPromptDirectory, "manager-system"),
        ["developer-implementation"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer-implementation"),
        ["developer-testing"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer-testing"),
        ["developer-write-tests"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer-write-tests"),
        ["manager-compaction-summary"] = LoadPromptFromDisk(resolvedPromptDirectory, "manager-compaction-summary"),
        ["manager-compaction-system"] = LoadPromptFromDisk(resolvedPromptDirectory, "manager-compaction-system"),
        ["developer-compaction-summary"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer-compaction-summary"),
        ["developer-compaction-system"] = LoadPromptFromDisk(resolvedPromptDirectory, "developer-compaction-system")
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
        PromptDirectory = resolvedPromptDirectory
    };

    return config;
}

static string LoadPromptFromDisk(string promptDirectory, string promptName)
{
    string filePath = Path.Combine(promptDirectory, $"{promptName}.txt");
    if (File.Exists(filePath))
    {
        return File.ReadAllText(filePath);
    }
    return string.Empty;
}

static WorkerSettings LoadWorkerSettings()
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

static string ResolveSettingsPath()
{
    string configFile = "env/settings.json";
    string resolvedPath = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, configFile));

    return resolvedPath;
}

static string ResolvePromptDirectory(string promptDirectory)
{
    return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, promptDirectory));
}

static ICompaction BuildCompaction(CompactionSettings settings, List<LLMConfig> llmConfigs, string summaryPrompt, string summarySystemPrompt)
{
    ICompaction compaction = new CompactionNone();

    if (string.Equals(settings.Type, "summarize", StringComparison.OrdinalIgnoreCase) && llmConfigs.Count > 0)
    {
        LLMConfig currentLlm = llmConfigs[0];
        LlmService summarizerService = new LlmService(currentLlm);
        Kernel summarizerKernel = summarizerService.CreateKernel(Array.Empty<object>());
        compaction = new CompactionSummarizer(
            summarizerService,
            summarizerKernel,
            summaryPrompt,
            summarySystemPrompt,
            settings.ContextSizeThreshold,
            currentLlm.ContextLength);
    }

    return compaction;
}

class WorkerOptions
{
    [Option("ticket-id", Required = true, HelpText = "Ticket id for the worker.")]
    public required string TicketId { get; set; }

    [Option("server-url", Required = true, HelpText = "Server URL for the worker.")]
    public required string ServerUrl { get; set; }
}
