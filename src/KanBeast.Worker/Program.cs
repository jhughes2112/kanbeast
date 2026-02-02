using System.Text.Json;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Agents;

Console.WriteLine("KanBeast Worker Starting...");

// Parse command line arguments or environment variables
var config = ParseConfiguration(args);

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
    var managerAgent = new ManagerAgent(apiClient, toolExecutor, config.ManagerPrompt);
    var developerAgent = new DeveloperAgent(toolExecutor, apiClient, config.DeveloperPrompt, ticket.Id);

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

static WorkerConfig? ParseConfiguration(string[] args)
{
    // In a real implementation, this would parse command line args or read from environment
    // For now, return a default configuration for testing
    
    var ticketId = GetArgValue(args, "--ticket-id");
    var serverUrl = GetArgValue(args, "--server-url") ?? "http://localhost:5000";
    
    if (string.IsNullOrEmpty(ticketId))
        return null;

    return new WorkerConfig
    {
        TicketId = ticketId,
        ServerUrl = serverUrl,
        GitConfig = new GitConfig
        {
            RepositoryUrl = GetArgValue(args, "--git-url") ?? "",
            Username = GetArgValue(args, "--git-username") ?? "KanBeast Worker",
            Email = GetArgValue(args, "--git-email") ?? "worker@kanbeast.local",
            SshKey = GetArgValue(args, "--git-ssh-key")
        },
        LLMConfigs = new List<LLMConfig>(),
        ManagerPrompt = "You are a project manager. Break down tasks and verify completion.",
        DeveloperPrompt = "You are a software developer. Implement features and write tests."
    };
}

static string? GetArgValue(string[] args, string key)
{
    var index = Array.IndexOf(args, key);
    if (index >= 0 && index + 1 < args.Length)
        return args[index + 1];
    return null;
}
