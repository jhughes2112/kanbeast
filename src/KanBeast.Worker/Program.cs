using System.Text.Json;
using CommandLine;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Services.Tools;
using KanBeast.Worker.Agents;

Console.WriteLine("KanBeast Worker Starting...");

// Parse command line arguments or environment variables
var config = Parser.Default.ParseArguments<WorkerOptions>(args)
    .MapResult(BuildConfiguration, _ => null);

if (config == null)
{
    Console.WriteLine("Error: Invalid configuration. Please provide worker configuration.");
    Console.WriteLine("Usage: KanBeast.Worker --ticket-id <id> --server-url <url> [options]");
    return 1;
}

Console.WriteLine($"Worker initialized for ticket: {config.TicketId}");

try
{
    // Initialize services
    var apiClient = new KanbanApiClient(config.ServerUrl);
    var gitService = new GitService();
    var toolExecutor = new ToolExecutor();
    ILlmService llmService = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds);

    // Fetch ticket details
    Console.WriteLine("Fetching ticket details...");
    var ticket = await apiClient.GetTicketAsync(config.TicketId);
    
    if (ticket == null)
    {
        Console.WriteLine($"Error: Ticket {config.TicketId} not found");
        return 1;
    }

    Console.WriteLine($"Ticket: {ticket.Title}");
    await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Initialized and starting work");

    // Setup working directory
    var workDir = Path.Combine(Path.GetTempPath(), $"kanbeast-{ticket.Id}");
    if (Directory.Exists(workDir))
    {
        Directory.Delete(workDir, true);
    }
    Directory.CreateDirectory(workDir);

    Console.WriteLine($"Working directory: {workDir}");

    // Clone repository
    if (!string.IsNullOrEmpty(config.GitConfig.RepositoryUrl))
    {
        Console.WriteLine("Cloning repository...");
        await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Cloning repository");
        
        try
        {
            var repoDir = Path.Combine(workDir, "repo");
            await gitService.CloneRepositoryAsync(config.GitConfig.RepositoryUrl, repoDir);
            await gitService.ConfigureGitAsync(config.GitConfig.Username, config.GitConfig.Email, repoDir);

            // Create or checkout branch
            var branchName = ticket.BranchName ?? $"feature/ticket-{ticket.Id}";
            Console.WriteLine($"Branch: {branchName}");
            await gitService.CreateOrCheckoutBranchAsync(branchName, repoDir);
            
            if (string.IsNullOrEmpty(ticket.BranchName))
            {
                await apiClient.SetBranchNameAsync(ticket.Id, branchName);
            }

            workDir = repoDir; // Update work directory to the repo
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Git operations failed: {ex.Message}");
            await apiClient.AddActivityLogAsync(ticket.Id, $"Worker: Git error - {ex.Message}");
        }
    }

    // Initialize agents
    var managerKernel = llmService.CreateKernel(new object[]
    {
        new ShellTools(toolExecutor),
        new FileTools(toolExecutor),
        new KanbanTools(apiClient)
    });

    var developerKernel = llmService.CreateKernel(new object[]
    {
        new ShellTools(toolExecutor),
        new FileTools(toolExecutor),
        new DeveloperTaskTools(apiClient)
    });

    var managerAgent = new ManagerAgent(apiClient, toolExecutor, config.ManagerPrompt, llmService, managerKernel);
    var developerAgent = new DeveloperAgent(toolExecutor, apiClient, config.DeveloperPrompt, ticket.Id, llmService, developerKernel);

    // Manager breaks down the ticket
    Console.WriteLine("Manager: Breaking down ticket into tasks...");
    var tasks = await managerAgent.BreakDownTicketAsync(ticket);
    
    // Refresh ticket to get the updated tasks
    ticket = await apiClient.GetTicketAsync(config.TicketId);

    // Work on each task
    foreach (var task in ticket!.Tasks)
    {
        foreach (var subtask in task.Subtasks)
        {
            if (subtask.Status == SubtaskStatus.Complete)
                continue;

            Console.WriteLine($"Working on subtask: {subtask.Name}");

            var success = await developerAgent.WorkOnTaskAsync(task, subtask, ticket.Id, workDir);

            if (!success)
            {
                return 1;
            }

            var refreshed = await apiClient.GetTicketAsync(config.TicketId);
            var refreshedTask = refreshed?.Tasks.FirstOrDefault(t => t.Id == task.Id);
            var refreshedSubtask = refreshedTask?.Subtasks.FirstOrDefault(s => s.Id == subtask.Id);

            if (refreshedSubtask?.Status != SubtaskStatus.Complete)
            {
                await apiClient.AddActivityLogAsync(ticket.Id, $"Manager: Subtask not marked complete - {subtask.Name}");
                return 1;
            }

            var verified = await managerAgent.VerifyTaskCompletionAsync(subtask.Name, workDir);
            if (!verified)
            {
                await apiClient.UpdateSubtaskStatusAsync(ticket.Id, task.Id, subtask.Id, SubtaskStatus.Incomplete);
                await apiClient.AddActivityLogAsync(ticket.Id, $"Manager: Verification failed for {subtask.Name}");
                return 1;
            }

            Console.WriteLine($"Subtask completed: {subtask.Name}");
        }
    }

    // Check if all tasks are complete
    ticket = await apiClient.GetTicketAsync(config.TicketId);
    if (ticket != null && await managerAgent.AllTasksCompleteAsync(ticket))
    {
        Console.WriteLine("All tasks complete! Moving to Testing...");
        await apiClient.UpdateTicketStatusAsync(ticket.Id, "Testing");
        
        // Commit and push changes
        if (!string.IsNullOrEmpty(config.GitConfig.RepositoryUrl))
        {
            try
            {
                await gitService.CommitChangesAsync($"Completed ticket: {ticket.Title}", workDir);
                await gitService.PushChangesAsync(workDir);
                await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Changes committed and pushed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Git commit/push failed: {ex.Message}");
                await apiClient.AddActivityLogAsync(ticket.Id, $"Worker: Git error - {ex.Message}");
            }
        }
    }

    Console.WriteLine("Worker completed successfully!");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Worker failed: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    return 1;
}

static WorkerConfig? BuildConfiguration(WorkerOptions options)
{
    WorkerConfig? config = null;
    string? ticketId = options.TicketId ?? GetEnvValue("TICKET_ID");
    string? serverUrl = options.ServerUrl ?? GetEnvValue("SERVER_URL") ?? "http://localhost:5000";

    if (!string.IsNullOrEmpty(ticketId))
    {
        string? llmConfigFile = options.LlmConfigFile ?? GetEnvValue("LLM_CONFIG_FILE");
        List<LLMConfig>? llmConfigs = LoadLlmConfigs(llmConfigFile);

        if (llmConfigs != null && llmConfigs.Count > 0)
        {
            int retryCount = GetIntOptionOrEnv(options.LlmRetryCount, "LLM_RETRY_COUNT", 0);
            int retryDelaySeconds = GetIntOptionOrEnv(options.LlmRetryDelaySeconds, "LLM_RETRY_DELAY_SECONDS", 0);

            string promptDirectory = "env/prompts";
            string resolvedPromptDirectory = ResolvePromptDirectory(promptDirectory);
            List<PromptTemplate> systemPrompts = LoadPromptTemplates(resolvedPromptDirectory);

            string managerPrompt = BuildManagerPrompt(systemPrompts);
            string developerPrompt = BuildDeveloperPrompt(systemPrompts);

            config = new WorkerConfig
            {
                TicketId = ticketId,
                ServerUrl = serverUrl,
                GitConfig = new GitConfig
                {
                    RepositoryUrl = options.GitUrl ?? GetEnvValue("GIT_URL") ?? string.Empty,
                    Username = options.GitUsername ?? GetEnvValue("GIT_USERNAME") ?? "KanBeast Worker",
                    Email = options.GitEmail ?? GetEnvValue("GIT_EMAIL") ?? "worker@kanbeast.local",
                    SshKey = options.GitSshKey ?? GetEnvValue("GIT_SSH_KEY")
                },
                LLMConfigs = llmConfigs,
                LlmRetryCount = retryCount,
                LlmRetryDelaySeconds = retryDelaySeconds,
                ManagerPrompt = string.IsNullOrWhiteSpace(managerPrompt)
                    ? "You are a project manager. Break down tasks and verify completion."
                    : managerPrompt,
                DeveloperPrompt = string.IsNullOrWhiteSpace(developerPrompt)
                    ? "You are a software developer. Implement features and write tests."
                    : developerPrompt,
                PromptDirectory = resolvedPromptDirectory
            };
        }
    }

    return config;
}

static List<LLMConfig>? LoadLlmConfigs(string? configFile)
{
    List<LLMConfig>? configs = null;

    if (!string.IsNullOrWhiteSpace(configFile))
    {
        string resolvedPath = ResolveLlmConfigPath(configFile);

        if (File.Exists(resolvedPath))
        {
            string json = File.ReadAllText(resolvedPath);
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            configs = JsonSerializer.Deserialize<List<LLMConfig>>(json, options);
        }
    }

    return configs;
}

static string ResolveLlmConfigPath(string configFile)
{
    string resolvedPath = Path.IsPathRooted(configFile)
        ? configFile
        : Path.Combine(AppContext.BaseDirectory, configFile);

    return resolvedPath;
}

static int GetIntOptionOrEnv(int? optionValue, string envKey, int defaultValue)
{
    int value = defaultValue;

    if (optionValue.HasValue)
    {
        value = optionValue.Value;
    }
    else
    {
        string? envValue = GetEnvValue(envKey);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            if (int.TryParse(envValue, out int parsed))
            {
                value = parsed;
            }
        }
    }

    return value;
}

static string? GetEnvValue(string key)
{
    return Environment.GetEnvironmentVariable(key);
}

static string ResolvePromptDirectory(string promptDirectory)
{
    return Path.IsPathRooted(promptDirectory)
        ? promptDirectory
        : Path.Combine(AppContext.BaseDirectory, promptDirectory);
}

static List<PromptTemplate> LoadPromptTemplates(string promptDirectory)
{
    var prompts = GetDefaultPromptTemplates();
    foreach (var prompt in prompts)
    {
        var path = Path.Combine(promptDirectory, prompt.FileName);
        if (File.Exists(path))
        {
            prompt.Content = File.ReadAllText(path);
        }
    }

    return prompts;
}

static T? ParseJsonEnv<T>(string key)
{
    var value = GetEnvValue(key);
    if (string.IsNullOrWhiteSpace(value))
    {
        return default;
    }

    try
    {
        return JsonSerializer.Deserialize<T>(value);
    }
    catch
    {
        return default;
    }
}

static string BuildManagerPrompt(List<PromptTemplate> prompts)
{
    var sections = new[]
    {
        "manager-master",
        "manager-breakdown",
        "manager-assign",
        "manager-verify",
        "manager-accept",
        "manager-testing"
    };

    return BuildPromptFromSections(prompts, sections);
}

static string BuildDeveloperPrompt(List<PromptTemplate> prompts)
{
    var sections = new[]
    {
        "developer-implementation",
        "developer-testing"
    };

    return BuildPromptFromSections(prompts, sections);
}

static string BuildPromptFromSections(IEnumerable<PromptTemplate> prompts, IEnumerable<string> keys)
{
    var content = keys
        .Select(key => prompts.FirstOrDefault(prompt => prompt.Key == key))
        .Where(prompt => prompt != null)
        .Select(prompt => string.IsNullOrWhiteSpace(prompt!.DisplayName)
            ? prompt.Content
            : $"{prompt.DisplayName}\n{prompt.Content}")
        .Where(promptContent => !string.IsNullOrWhiteSpace(promptContent))
        .ToList();

    return string.Join("\n\n", content);
}

static List<PromptTemplate> GetDefaultPromptTemplates()
{
    return new List<PromptTemplate>
    {
        new()
        {
            Key = "manager-master",
            DisplayName = "Manager: Mode Selection",
            FileName = "manager-master.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "manager-breakdown",
            DisplayName = "Manager: Break Down Ticket",
            FileName = "manager-breakdown.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "manager-assign",
            DisplayName = "Manager: Assign Task",
            FileName = "manager-assign.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "manager-verify",
            DisplayName = "Manager: Verify Task",
            FileName = "manager-verify.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "manager-accept",
            DisplayName = "Manager: Accept Task",
            FileName = "manager-accept.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "manager-testing",
            DisplayName = "Manager: Testing Phase",
            FileName = "manager-testing.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "developer-implementation",
            DisplayName = "Developer: Implement Features",
            FileName = "developer-implementation.txt",
            Content = string.Empty
        },
        new()
        {
            Key = "developer-testing",
            DisplayName = "Developer: Test Changes",
            FileName = "developer-testing.txt",
            Content = string.Empty
        }
    };
}

class WorkerOptions
{
    [Option("ticket-id", HelpText = "Ticket id for the worker.")]
    public string? TicketId { get; set; }

    [Option("server-url", HelpText = "Server URL for the worker.")]
    public string? ServerUrl { get; set; }

    [Option("git-url", HelpText = "Git repository URL.")]
    public string? GitUrl { get; set; }

    [Option("git-username", HelpText = "Git username.")]
    public string? GitUsername { get; set; }

    [Option("git-email", HelpText = "Git email.")]
    public string? GitEmail { get; set; }

    [Option("git-ssh-key", HelpText = "Git SSH key content.")]
    public string? GitSshKey { get; set; }

    [Option("llm-config-file", HelpText = "Path to a JSON list of LLM endpoints.")]
    public string? LlmConfigFile { get; set; }

    [Option("llm-retry-count", HelpText = "Number of retries before falling back to the next LLM.")]
    public int? LlmRetryCount { get; set; }

    [Option("llm-retry-delay-seconds", HelpText = "Delay between retries in seconds.")]
    public int? LlmRetryDelaySeconds { get; set; }

}
