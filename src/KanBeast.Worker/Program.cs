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
    var llmService = new LlmService(config.LLMConfigs);

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
        new FileTools(toolExecutor)
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
        if (task.IsCompleted)
            continue;

        Console.WriteLine($"Working on task: {task.Description}");
        
        // Developer works on the task
        var success = await developerAgent.WorkOnTaskAsync(task.Description, workDir);
        
        if (success)
        {
            // Manager verifies completion
            var verified = await managerAgent.VerifyTaskCompletionAsync(task.Description, workDir);
            
            if (verified)
            {
                await apiClient.UpdateTaskStatusAsync(ticket.Id, task.Id, true);
                Console.WriteLine($"Task completed: {task.Description}");
            }
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
    var ticketId = options.TicketId ?? GetEnvValue("TICKET_ID");
    var serverUrl = options.ServerUrl ?? GetEnvValue("SERVER_URL") ?? "http://localhost:5000";

    if (string.IsNullOrEmpty(ticketId))
        return null;

    var promptDirectory = "env/prompts";
    var resolvedPromptDirectory = ResolvePromptDirectory(promptDirectory);
    var systemPrompts = LoadPromptTemplates(resolvedPromptDirectory);

    var managerPrompt = BuildManagerPrompt(systemPrompts);
    var developerPrompt = BuildDeveloperPrompt(systemPrompts);

    return new WorkerConfig
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
        LLMConfigs = ParseJsonEnv<List<LLMConfig>>("LLM_CONFIGS") ?? new List<LLMConfig>(),
        ManagerPrompt = string.IsNullOrWhiteSpace(managerPrompt)
            ? "You are a project manager. Break down tasks and verify completion."
            : managerPrompt,
        DeveloperPrompt = string.IsNullOrWhiteSpace(developerPrompt)
            ? "You are a software developer. Implement features and write tests."
            : developerPrompt,
        PromptDirectory = resolvedPromptDirectory
    };
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

    [Option("prompt-dir", HelpText = "Directory containing prompt files.")]
    public string? PromptDirectory { get; set; }

}
