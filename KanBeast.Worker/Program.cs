using System.Text.Json;
using CommandLine;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using KanBeast.Worker.Services.Tools;
using Microsoft.SemanticKernel;

Console.WriteLine("KanBeast Worker Starting...");

// Parse command line arguments or environment variables
WorkerConfig? config = null;
try
{
    config = Parser.Default.ParseArguments<WorkerOptions>(args)
        .MapResult(
            BuildConfiguration,
            errors =>
            {
                Console.WriteLine("Error: Failed to parse command line arguments.");
                throw new InvalidOperationException("Failed to parse command line arguments.");
            });
}
catch (Exception ex)
{
    Console.WriteLine($"Configuration error: {ex.Message}");
    Console.WriteLine("Usage: KanBeast.Worker --ticket-id <id> --server-url <url> [options]");
    Console.WriteLine("Sleeping 100 seconds before exit to allow log inspection...");
    await Task.Delay(TimeSpan.FromSeconds(100));
    return 1;
}

Console.WriteLine($"Worker initialized for ticket: {config.TicketId}");

try
{
    // Initialize services
    KanbanApiClient apiClient = new KanbanApiClient(config.ServerUrl);
    GitService gitService = new GitService(config.GitConfig);
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
    string workDir = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "repo"));
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
            await gitService.CloneRepositoryAsync(config.GitConfig.RepositoryUrl, workDir);
            await gitService.ConfigureGitAsync(config.GitConfig.Username, config.GitConfig.Email, workDir);

            // Create or checkout branch
            string branchName = ticket.BranchName ?? $"feature/ticket-{ticket.Id}";
            Console.WriteLine($"Branch: {branchName}");
            await gitService.CreateOrCheckoutBranchAsync(branchName, workDir);

            if (string.IsNullOrEmpty(ticket.BranchName))
            {
                await apiClient.SetBranchNameAsync(ticket.Id, branchName);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Git operations failed: {ex.Message}");
            await apiClient.AddActivityLogAsync(ticket.Id, $"Worker: Git error - {ex.Message}");
        }
    }

    // Initialize and run the agent orchestrator
    AgentOrchestrator orchestrator = new AgentOrchestrator(
        apiClient,
        managerLlmService,
        developerLlmService,
        toolExecutor,
        gitService,
        config.ManagerPrompt,
        config.DeveloperImplementationPrompt,
        config.DeveloperTestingPrompt,
        config.DeveloperWriteTestsPrompt,
        config.MaxIterationsPerSubtask);

    Console.WriteLine("Starting agent orchestrator...");
    await orchestrator.RunAsync(ticket, workDir, CancellationToken.None);

    // Commit and push changes after completion
    if (!string.IsNullOrEmpty(config.GitConfig.RepositoryUrl))
    {
        try
        {
            TicketDto? finalTicket = await apiClient.GetTicketAsync(config.TicketId);
            if (finalTicket != null && finalTicket.Status == "Done")
            {
                await gitService.CommitChangesAsync($"Completed ticket: {finalTicket.Title}", workDir);
                await gitService.PushChangesAsync(workDir);
                await apiClient.AddActivityLogAsync(finalTicket.Id, "Worker: Changes committed and pushed");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Git commit/push failed: {ex.Message}");
            await apiClient.AddActivityLogAsync(ticket.Id, $"Worker: Git error - {ex.Message}");
        }
    }

    Console.WriteLine("Worker completed successfully!");
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine($"Worker failed: {ex.Message}");
    Console.WriteLine(ex.StackTrace);
    Console.WriteLine("Sleeping 100 seconds before exit to allow log inspection...");
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
    List<PromptTemplate> systemPrompts = LoadPromptTemplates(resolvedPromptDirectory);

    string managerPrompt = BuildManagerPrompt(systemPrompts);
    string developerPrompt = BuildDeveloperPrompt(systemPrompts);
    string developerImplementationPrompt = GetPromptContent(systemPrompts, "developer-implementation", true);
    string developerTestingPrompt = GetPromptContent(systemPrompts, "developer-testing", true);
    string developerWriteTestsPrompt = GetPromptContent(systemPrompts, "developer-write-tests", false);

    if (string.IsNullOrWhiteSpace(managerPrompt))
    {
        Console.WriteLine("Error: Manager prompt is empty after loading templates");
        throw new InvalidOperationException("Manager prompt is empty. Ensure manager prompt files exist.");
    }

    if (string.IsNullOrWhiteSpace(developerImplementationPrompt))
    {
        Console.WriteLine("Error: Developer implementation prompt is empty");
        throw new InvalidOperationException("Developer implementation prompt is empty.");
    }

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
        ManagerCompactionSummaryPrompt = GetPromptContent(systemPrompts, "manager-compaction-summary", false),
        ManagerCompactionSystemPrompt = GetPromptContent(systemPrompts, "manager-compaction-system", false),
        DeveloperCompactionSummaryPrompt = GetPromptContent(systemPrompts, "developer-compaction-summary", false),
        DeveloperCompactionSystemPrompt = GetPromptContent(systemPrompts, "developer-compaction-system", false),
        ManagerPrompt = managerPrompt,
        DeveloperPrompt = developerPrompt,
        DeveloperImplementationPrompt = developerImplementationPrompt,
        DeveloperTestingPrompt = developerTestingPrompt,
        DeveloperWriteTestsPrompt = string.IsNullOrWhiteSpace(developerWriteTestsPrompt)
            ? developerImplementationPrompt
            : developerWriteTestsPrompt,
        PromptDirectory = resolvedPromptDirectory
    };

    return config;
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

static string GetPromptContent(List<PromptTemplate> prompts, string key, bool required)
{
    foreach (PromptTemplate prompt in prompts)
    {
        if (string.Equals(prompt.Key, key, StringComparison.Ordinal))
        {
            if (required && string.IsNullOrWhiteSpace(prompt.Content))
            {
                Console.WriteLine($"Error: Required prompt '{key}' has no content (file: {prompt.FileName})");
                throw new InvalidOperationException($"Required prompt '{key}' has no content. Ensure {prompt.FileName} exists and is not empty.");
            }

            return prompt.Content;
        }
    }

    if (required)
    {
        Console.WriteLine($"Error: Required prompt '{key}' not found in templates");
        throw new InvalidOperationException($"Required prompt '{key}' not found in templates.");
    }

    return string.Empty;
}

static List<PromptTemplate> LoadPromptTemplates(string promptDirectory)
{
    if (!Directory.Exists(promptDirectory))
    {
        Console.WriteLine($"Error: Prompt directory not found: {promptDirectory}");
        throw new DirectoryNotFoundException($"Prompt directory not found: {promptDirectory}");
    }

    List<PromptTemplate> prompts = GetDefaultPromptTemplates();
    List<string> missingFiles = new List<string>();

    foreach (PromptTemplate prompt in prompts)
    {
        string path = Path.Combine(promptDirectory, prompt.FileName);

        if (File.Exists(path))
        {
            prompt.Content = File.ReadAllText(path);
        }
        else
        {
            missingFiles.Add(prompt.FileName);
            Console.WriteLine($"Warning: Prompt file not found, using default: {path}");
        }
    }

    // Core prompts that must exist (not use defaults)
    string[] requiredPrompts = new[]
    {
        "manager-master.txt",
        "manager-breakdown.txt",
        "manager-assign.txt",
        "manager-verify.txt",
        "developer-implementation.txt",
        "developer-testing.txt"
    };

    List<string> missingRequired = new List<string>();
    foreach (string required in requiredPrompts)
    {
        foreach (string missing in missingFiles)
        {
            if (string.Equals(missing, required, StringComparison.Ordinal))
            {
                missingRequired.Add(required);
                break;
            }
        }
    }

    if (missingRequired.Count > 0)
    {
        string missingList = string.Join(", ", missingRequired);
        Console.WriteLine($"Error: Required prompt files missing: {missingList}");
        throw new FileNotFoundException($"Required prompt files missing in {promptDirectory}: {missingList}");
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
        "manager-testing",
        "manager-blocked"
    };

    return BuildPromptFromSections(prompts, sections);
}

static string BuildDeveloperPrompt(List<PromptTemplate> prompts)
{
    // Return a combined prompt for backward compatibility
    // The orchestrator uses individual prompts from config
    string[] sections = new[]
    {
        "developer-implementation",
        "developer-testing",
        "developer-write-tests"
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
            Key = "developer-write-tests",
            DisplayName = "Developer: Write Tests",
            FileName = "developer-write-tests.txt",
            Content = "Role: Developer agent in write-tests mode.\n\nProcess:\n- Inspect existing tests to understand patterns.\n- Write or update unit tests for the specified functionality.\n- Run tests to ensure they pass.\n\nOutput:\n- Report tests created and results."
        },
        new()
        {
            Key = "manager-blocked",
            DisplayName = "Manager: Blocked State",
            FileName = "manager-blocked.txt",
            Content = "Role: Manager agent in Blocked mode.\n\nProcess:\n- Document the blocker and what was tried.\n- Specify what is needed to unblock.\n- Await human input before resuming."
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
    [Option("ticket-id", Required = true, HelpText = "Ticket id for the worker.")]
    public required string TicketId { get; set; }

    [Option("server-url", Required = true, HelpText = "Server URL for the worker.")]
    public required string ServerUrl { get; set; }
}
