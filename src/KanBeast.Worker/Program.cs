using System.Text.Json;
using CommandLine;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Services.Tools;
using KanBeast.Worker.Agents;
using Microsoft.SemanticKernel;

Console.WriteLine("KanBeast Worker Starting...");

// Parse command line arguments or environment variables
WorkerConfig? config = Parser.Default.ParseArguments<WorkerOptions>(args)
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
    KanbanApiClient apiClient = new KanbanApiClient(config.ServerUrl);
    GitService gitService = new GitService();
    ToolExecutor toolExecutor = new ToolExecutor();
    HashSet<int> downedLlmIndices = new HashSet<int>();
    ICompaction managerCompaction = BuildCompaction(
        config.ManagerCompaction,
        config.LLMConfigs,
        config.ManagerCompactionSummaryPrompt,
        config.ManagerCompactionSystemPrompt);
    ICompaction developerCompaction = BuildCompaction(
        config.DeveloperCompaction,
        config.LLMConfigs,
        config.DeveloperCompactionSummaryPrompt,
        config.DeveloperCompactionSystemPrompt);
    ILlmService managerLlmService = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds, downedLlmIndices, managerCompaction);
    ILlmService developerLlmService = new LlmProxy(config.LLMConfigs, config.LlmRetryCount, config.LlmRetryDelaySeconds, downedLlmIndices, developerCompaction);

    // Fetch ticket details
    Console.WriteLine("Fetching ticket details...");
    TicketDto? ticket = await apiClient.GetTicketAsync(config.TicketId);
    
    if (ticket == null)
    {
        Console.WriteLine($"Error: Ticket {config.TicketId} not found");
        return 1;
    }

    Console.WriteLine($"Ticket: {ticket.Title}");
    await apiClient.AddActivityLogAsync(ticket.Id, "Worker: Initialized and starting work");

    // Setup working directory
    string workDir = Path.Combine(Path.GetTempPath(), $"kanbeast-{ticket.Id}");
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
            string repoDir = Path.Combine(workDir, "repo");
            await gitService.CloneRepositoryAsync(config.GitConfig.RepositoryUrl, repoDir);
            await gitService.ConfigureGitAsync(config.GitConfig.Username, config.GitConfig.Email, repoDir);

            // Create or checkout branch
            string branchName = ticket.BranchName ?? $"feature/ticket-{ticket.Id}";
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
    Kernel managerKernel = managerLlmService.CreateKernel(new object[]
    {
        new ShellTools(toolExecutor),
        new FileTools(toolExecutor),
        new KanbanTools(apiClient)
    });

    Kernel developerKernel = developerLlmService.CreateKernel(new object[]
    {
        new ShellTools(toolExecutor),
        new FileTools(toolExecutor),
        new DeveloperTaskTools(apiClient)
    });

    ManagerAgent managerAgent = new ManagerAgent(apiClient, toolExecutor, config.ManagerPrompt, managerLlmService, managerKernel);
    DeveloperAgent developerAgent = new DeveloperAgent(toolExecutor, apiClient, config.DeveloperPrompt, ticket.Id, developerLlmService, developerKernel);

    // Manager breaks down the ticket
    Console.WriteLine("Manager: Breaking down ticket into tasks...");
    List<string> tasks = await managerAgent.BreakDownTicketAsync(ticket);
    
    // Refresh ticket to get the updated tasks
    ticket = await apiClient.GetTicketAsync(config.TicketId);

    // Work on each task
    foreach (KanbanTaskDto task in ticket!.Tasks)
    {
        foreach (KanbanSubtaskDto subtask in task.Subtasks)
        {
            if (subtask.Status == SubtaskStatus.Complete)
                continue;

            Console.WriteLine($"Working on subtask: {subtask.Name}");

            bool success = await developerAgent.WorkOnTaskAsync(task, subtask, ticket.Id, workDir);

            if (!success)
            {
                return 1;
            }

            TicketDto? refreshed = await apiClient.GetTicketAsync(config.TicketId);
            KanbanTaskDto? refreshedTask = null;
            KanbanSubtaskDto? refreshedSubtask = null;

            if (refreshed != null)
            {
                foreach (KanbanTaskDto candidate in refreshed.Tasks)
                {
                    if (string.Equals(candidate.Id, task.Id, StringComparison.Ordinal))
                    {
                        refreshedTask = candidate;
                        break;
                    }
                }

                if (refreshedTask != null)
                {
                    foreach (KanbanSubtaskDto candidate in refreshedTask.Subtasks)
                    {
                        if (string.Equals(candidate.Id, subtask.Id, StringComparison.Ordinal))
                        {
                            refreshedSubtask = candidate;
                            break;
                        }
                    }
                }
            }

            if (refreshedSubtask?.Status != SubtaskStatus.Complete)
            {
                await apiClient.AddActivityLogAsync(ticket.Id, $"Manager: Subtask not marked complete - {subtask.Name}");
                return 1;
            }

            bool verified = await managerAgent.VerifyTaskCompletionAsync(subtask.Name, workDir);
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
        WorkerSettings? settings = LoadWorkerSettings();

        if (settings != null && settings.LLMConfigs.Count > 0)
        {
            string promptDirectory = "env/prompts";
            string resolvedPromptDirectory = ResolvePromptDirectory(promptDirectory);
            List<PromptTemplate> systemPrompts = LoadPromptTemplates(resolvedPromptDirectory);

            string managerPrompt = BuildManagerPrompt(systemPrompts);
            string developerPrompt = BuildDeveloperPrompt(systemPrompts);

            config = new WorkerConfig
            {
                TicketId = ticketId,
                ServerUrl = serverUrl,
                GitConfig = settings.GitConfig,
                LLMConfigs = settings.LLMConfigs,
                LlmRetryCount = settings.LlmRetryCount,
                LlmRetryDelaySeconds = settings.LlmRetryDelaySeconds,
                ManagerCompaction = settings.ManagerCompaction,
                DeveloperCompaction = settings.DeveloperCompaction,
                ManagerCompactionSummaryPrompt = GetPromptContent(systemPrompts, "manager-compaction-summary"),
                ManagerCompactionSystemPrompt = GetPromptContent(systemPrompts, "manager-compaction-system"),
                DeveloperCompactionSummaryPrompt = GetPromptContent(systemPrompts, "developer-compaction-summary"),
                DeveloperCompactionSystemPrompt = GetPromptContent(systemPrompts, "developer-compaction-system"),
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

static WorkerSettings? LoadWorkerSettings()
{
    WorkerSettings? settings = null;
    string resolvedPath = ResolveSettingsPath();

    if (File.Exists(resolvedPath))
    {
        string json = File.ReadAllText(resolvedPath);
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        settings = JsonSerializer.Deserialize<WorkerSettings>(json, options);
    }

    return settings;
}

static string ResolveSettingsPath()
{
    string configFile = "env/settings.json";
    string resolvedPath = Path.IsPathRooted(configFile)
        ? configFile
        : Path.Combine(AppContext.BaseDirectory, configFile);

    return resolvedPath;
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

static ICompaction BuildCompaction(CompactionSettings settings, List<LLMConfig> llmConfigs, string summaryPrompt, string summarySystemPrompt)
{
    ICompaction compaction = new CompactionNone();

    if (string.Equals(settings.Type, "summarizer", StringComparison.OrdinalIgnoreCase))
    {
        LLMConfig summarizerConfig = llmConfigs[settings.SummarizerConfigIndex];
        LlmService summarizerService = new LlmService(summarizerConfig);
        Kernel summarizerKernel = summarizerService.CreateKernel(Array.Empty<object>());
        compaction = new CompactionSummarizer(
            summarizerService,
            summarizerKernel,
            summaryPrompt,
            summarySystemPrompt,
            settings.ContextSizeThreshold);
    }

    return compaction;
}

static string GetPromptContent(List<PromptTemplate> prompts, string key)
{
    string content = string.Empty;

    foreach (PromptTemplate prompt in prompts)
    {
        if (string.Equals(prompt.Key, key, StringComparison.Ordinal))
        {
            content = prompt.Content;
            break;
        }
    }

    return content;
}

static List<PromptTemplate> LoadPromptTemplates(string promptDirectory)
{
    List<PromptTemplate> prompts = GetDefaultPromptTemplates();
    foreach (PromptTemplate prompt in prompts)
    {
        string path = Path.Combine(promptDirectory, prompt.FileName);
        if (File.Exists(path))
        {
            prompt.Content = File.ReadAllText(path);
        }
    }

    return prompts;
}

static string BuildManagerPrompt(List<PromptTemplate> prompts)
{
    string[] sections = new[]
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
    string[] sections = new[]
    {
        "developer-implementation",
        "developer-testing"
    };

    return BuildPromptFromSections(prompts, sections);
}

static string BuildPromptFromSections(IEnumerable<PromptTemplate> prompts, IEnumerable<string> keys)
{
    List<string> content = new List<string>();

    foreach (string key in keys)
    {
        PromptTemplate? selectedPrompt = null;

        foreach (PromptTemplate prompt in prompts)
        {
            if (string.Equals(prompt.Key, key, StringComparison.Ordinal))
            {
                selectedPrompt = prompt;
                break;
            }
        }

        if (selectedPrompt != null)
        {
            string promptContent = string.IsNullOrWhiteSpace(selectedPrompt.DisplayName)
                ? selectedPrompt.Content
                : $"{selectedPrompt.DisplayName}\n{selectedPrompt.Content}";

            if (!string.IsNullOrWhiteSpace(promptContent))
            {
                content.Add(promptContent);
            }
        }
    }

    return string.Join("\n\n", content);
}

static List<PromptTemplate> GetDefaultPromptTemplates()
{
    List<PromptTemplate> templates = new List<PromptTemplate>
    {
        new()
        {
            Key = "manager-master",
            DisplayName = "Manager: Mode Selection",
            FileName = "manager-master.txt",
            Content = "Role: Manager agent for KanBeast.\n\nPurpose:\n- Decide the current workflow mode from ticket status, tasks, activity log, and test results.\n- Choose one mode: Breakdown, Assign, Verify, Accept, Testing.\n\nProcess:\n1) State the chosen mode and why.\n2) Follow the matching mode prompt.\n3) If evidence is insufficient, ask for clarification and stop.\n\nTone: concise, direct, and outcome-focused."
        },
        new()
        {
            Key = "manager-breakdown",
            DisplayName = "Manager: Break Down Ticket",
            FileName = "manager-breakdown.txt",
            Content = "Role: Manager agent in Breakdown mode.\n\nOutput:\n- Provide an ordered list of small, testable subtasks.\n- Each task includes acceptance criteria and any key constraints.\n\nRules:\n- Keep tasks independent and measurable.\n- Avoid vague phrasing.\n- Ask for missing details instead of guessing."
        },
        new()
        {
            Key = "manager-assign",
            DisplayName = "Manager: Assign Task",
            FileName = "manager-assign.txt",
            Content = "Role: Manager agent in Assign mode.\n\nOutput:\n- Select the next incomplete task.\n- Restate the goal and acceptance criteria.\n- List constraints and relevant files to inspect.\n\nProcess:\n- Confirm readiness before work starts.\n- Do not assign multiple tasks at once."
        },
        new()
        {
            Key = "manager-verify",
            DisplayName = "Manager: Verify Task",
            FileName = "manager-verify.txt",
            Content = "Role: Manager agent in Verify mode.\n\nProcess:\n- Verify work against acceptance criteria.\n- Use tools when needed to check files, outputs, and tests.\n\nOutput:\n- Respond with APPROVED or REJECTED: <reason>.\n- If rejected, provide precise, actionable fixes."
        },
        new()
        {
            Key = "manager-accept",
            DisplayName = "Manager: Accept Task",
            FileName = "manager-accept.txt",
            Content = "Role: Manager agent in Accept mode.\n\nProcess:\n- Mark the verified task complete and update the ticket.\n- Move to the next task or transition to Testing if all tasks are complete.\n\nOutput:\n- Provide a short completion summary and next step."
        },
        new()
        {
            Key = "manager-testing",
            DisplayName = "Manager: Testing Phase",
            FileName = "manager-testing.txt",
            Content = "Role: Manager agent in Testing mode.\n\nProcess:\n- Ensure relevant tests are present and executed.\n- If tests fail, return to Active with remediation steps.\n- Only mark Done after all tests pass and changes are ready to commit."
        },
        new()
        {
            Key = "developer-implementation",
            DisplayName = "Developer: Implement Features",
            FileName = "developer-implementation.txt",
            Content = "Role: Developer agent implementing tasks in a codebase.\n\nProcess:\n- Read relevant files before editing.\n- Follow repository conventions and minimize changes.\n- Use available tools for file edits and commands.\n\nOutput:\n- Summarize changes and test results.\n- If blocked, explain what is missing."
        },
        new()
        {
            Key = "developer-testing",
            DisplayName = "Developer: Test Changes",
            FileName = "developer-testing.txt",
            Content = "Role: Developer agent in testing mode.\n\nProcess:\n- Add or update tests as needed.\n- Run relevant test suites and analyze failures.\n- Fix issues and re-run until green.\n\nOutput:\n- Report test commands and results."
        },
        new()
        {
            Key = "manager-compaction-summary",
            DisplayName = "Manager: Compaction Summary Prompt",
            FileName = "manager-compaction-summary.txt",
            Content = "Write a continuation summary for the manager agent.\n\nInclude:\n1) Task overview and success criteria.\n2) Completed work and verified outcomes.\n3) Open issues, risks, or blockers.\n4) Next steps in priority order.\n5) Key context that must be preserved.\n\nKeep it concise, factual, and actionable. Wrap the summary in <summary></summary> tags."
        },
        new()
        {
            Key = "manager-compaction-system",
            DisplayName = "Manager: Compaction System Prompt",
            FileName = "manager-compaction-system.txt",
            Content = "You summarize manager context so work can continue after compaction. Keep output concise, structured, and factual. Do not invent details."
        },
        new()
        {
            Key = "developer-compaction-summary",
            DisplayName = "Developer: Compaction Summary Prompt",
            FileName = "developer-compaction-summary.txt",
            Content = "Write a continuation summary for the developer agent.\n\nInclude:\n1) Task overview and acceptance criteria.\n2) Code changes made (files and key edits).\n3) Tests run and results.\n4) Problems found and fixes applied.\n5) Remaining work and next steps.\n\nKeep it concise, factual, and actionable. Wrap the summary in <summary></summary> tags."
        },
        new()
        {
            Key = "developer-compaction-system",
            DisplayName = "Developer: Compaction System Prompt",
            FileName = "developer-compaction-system.txt",
            Content = "You summarize developer context so work can continue after compaction. Keep output concise, structured, and factual. Do not invent details."
        }
    };

    return templates;
}

// Captures supported command-line options for the worker process.
class WorkerOptions
{
    [Option("ticket-id", HelpText = "Ticket id for the worker.")]
    public string? TicketId { get; set; }

    [Option("server-url", HelpText = "Server URL for the worker.")]
    public string? ServerUrl { get; set; }

}
