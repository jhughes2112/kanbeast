using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services.Tools;

// Configuration for running the developer LLM from within TicketTools.
public class DeveloperConfig
{
    public required LlmProxy LlmProxy { get; init; }
    public required string Prompt { get; init; }
    public required string WorkDir { get; init; }
    public required int MaxIterationsPerSubtask { get; init; }
    public required int StuckPromptingEvery { get; init; }
    public required Func<List<IToolProvider>> ToolProvidersFactory { get; init; }
}

// Tools for LLM to interact with the ticket system: logging, subtasks, status updates.
public class TicketTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger? _logger;
    private readonly IKanbanApiClient _apiClient;
    private readonly TicketHolder _ticketHolder;
    private readonly TaskState _state;
    private readonly DeveloperConfig? _developerConfig;
    private readonly Dictionary<LlmRole, List<Tool>> _toolsByRole;

    public TicketTools(IKanbanApiClient apiClient, TicketHolder ticketHolder, TaskState state)
        : this(null, apiClient, ticketHolder, state, null)
    {
    }

    public TicketTools(
        ILogger? logger,
        IKanbanApiClient apiClient,
        TicketHolder ticketHolder,
        TaskState state,
        DeveloperConfig? developerConfig)
    {
        _logger = logger;
        _apiClient = apiClient;
        _ticketHolder = ticketHolder;
        _state = state;
        _developerConfig = developerConfig;
        _toolsByRole = BuildToolsByRole();
    }

    private Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
    {
        List<Tool> managerTools = new List<Tool>();
        ToolHelper.AddTools(managerTools, this,
            nameof(LogMessageAsync),
            nameof(CreateTaskAsync),
            nameof(CreateSubtaskAsync),
            nameof(MarkTaskCompleteAsync),
            nameof(AssignSubtaskToDeveloperAsync),
            nameof(ApproveSubtaskAsync),
            nameof(RejectSubtaskAsync),
            nameof(MarkTicketCompleteAsync),
            nameof(MarkTicketBlockedAsync));

        List<Tool> developerTools = new List<Tool>();
        ToolHelper.AddTools(developerTools, this,
            nameof(LogMessageAsync),
            nameof(MarkSubtaskCompleteAsync));

        Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
        {
            [LlmRole.Manager] = managerTools,
            [LlmRole.Developer] = developerTools,
            [LlmRole.Compaction] = new List<Tool>()
        };

        return result;
    }

    public void AddTools(List<Tool> tools, LlmRole role)
    {
        if (_toolsByRole.TryGetValue(role, out List<Tool>? roleTools))
        {
            tools.AddRange(roleTools);
        }
        else
        {
            throw new ArgumentException($"Unhandled role: {role}");
        }
    }

    [Description("Write a message to the ticket activity log.")]
    public async Task<string> LogMessageAsync(
        [Description("Message to log")] string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return "Error: Message cannot be empty";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, message);
            return "Message logged";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while logging message";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to log message: {ex.Message}";
        }
    }

    // Looks up a task ID by name from the current ticket state.
    private string? FindTaskIdByName(string taskName)
    {
        foreach (KanbanTaskDto task in _ticketHolder.Ticket.Tasks)
        {
            if (string.Equals(task.Name, taskName, StringComparison.Ordinal))
            {
                return task.Id;
            }
        }

        return null;
    }

    [Description("Mark a task as complete when all of its subtasks are done.")]
    public async Task<string> MarkTaskCompleteAsync(
        [Description("Name of the task to mark complete")] string taskName)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = "Error: Task name cannot be empty";
        }
        else
        {
            string? taskId = FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = $"Error: Task '{taskName}' not found";
            }
            else
            {
                try
                {
                    using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                    TicketDto? updated = await _apiClient.MarkTaskCompleteAsync(_ticketHolder.Ticket.Id, taskId);

                    if (updated == null)
                    {
                        result = "Error: API returned null when marking task complete";
                    }
                    else
                    {
                        _ticketHolder.Update(updated);
                        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Task '{taskName}' marked complete");
                        result = $"Task '{taskName}' marked complete";
                    }
                }
                catch (TaskCanceledException)
                {
                    result = "Error: Request timed out while marking task complete";
                }
                catch (Exception ex)
                {
                    result = $"Error: Failed to mark task complete: {ex.Message}";
                }
            }
        }

        return result;
    }

    [Description("Create a new task for the ticket.")]
    public async Task<string> CreateTaskAsync(
        [Description("Name of the task")] string taskName,
        [Description("Description of the task")] string taskDescription)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = "Error: Task name cannot be empty";
        }
        else
        {
            try
            {
                KanbanTask task = new KanbanTask
                {
                    Name = taskName,
                    Description = taskDescription,
                    Subtasks = new List<KanbanSubtask>()
                };

                using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                TicketDto? updated = await _apiClient.AddTaskToTicketAsync(_ticketHolder.Ticket.Id, task);

                if (updated == null)
                {
                    result = "Error: API returned null when creating task";
                }
                else
                {
                    _ticketHolder.Update(updated);
                    await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created task '{taskName}'");

                    result = FormatTicketSummary(updated, $"SUCCESS: Created task '{taskName}'");
                }
            }
            catch (TaskCanceledException)
            {
                result = "Error: Request timed out while creating task";
            }
            catch (Exception ex)
            {
                result = $"Error: Failed to create task: {ex.Message}";
            }
        }

        return result;
    }

    [Description("Create a subtask for an existing task. Include clear acceptance criteria in the description so the developer knows exactly what 'done' looks like.")]
    public async Task<string> CreateSubtaskAsync(
        [Description("Name of the task to add the subtask to")] string taskName,
        [Description("Short name for the subtask")] string subtaskName,
        [Description("Detailed description including: what to do, acceptance criteria (how to verify it's done), and any constraints or notes")] string subtaskDescription)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = "Error: Task name cannot be empty";
        }
        else if (string.IsNullOrWhiteSpace(subtaskName))
        {
            result = "Error: Subtask name cannot be empty";
        }
        else
        {
            string? taskId = FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = $"Error: Task '{taskName}' not found";
            }
            else
            {
                try
                {
                    KanbanSubtask subtask = new KanbanSubtask
                    {
                        Name = subtaskName,
                        Description = subtaskDescription,
                        Status = SubtaskStatus.Incomplete
                    };

                    using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                    TicketDto? updated = await _apiClient.AddSubtaskToTaskAsync(_ticketHolder.Ticket.Id, taskId, subtask);

                    if (updated == null)
                    {
                        result = "Error: API returned null when creating subtask";
                    }
                    else
                    {
                        _ticketHolder.Update(updated);
                        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created subtask '{subtaskName}' under task '{taskName}'");

                        _state.SubtasksCreated = true;
                        _state.SubtaskCount += 1;

                        result = FormatTicketSummary(updated, $"SUCCESS: Created subtask '{subtaskName}' under task '{taskName}'");
                    }
                }
                catch (TaskCanceledException)
                {
                    result = "Error: Request timed out while creating subtask";
                }
                catch (Exception ex)
                {
                    result = $"Error: Failed to create subtask: {ex.Message}";
                }
            }
        }

        return result;
    }

    // Formats ticket state for LLM context (excludes activity log to save tokens).
    private static string FormatTicketSummary(TicketDto ticket, string header)
    {
        System.Text.StringBuilder sb = new System.Text.StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine($"Ticket: {ticket.Title} (Status: {ticket.Status})");
        sb.AppendLine("Tasks:");

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            sb.AppendLine($"  - {task.Name}");
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                sb.AppendLine($"      [{subtask.Status}] {subtask.Name}");
            }
        }

        return sb.ToString();
    }

    [Description("Assign the current subtask to the developer and run the developer agent to complete it.")]
    public async Task<string> AssignSubtaskToDeveloperAsync(
        [Description("Mode: 'implementation', 'testing', or 'write-tests'")] string mode,
        [Description("Clear goal statement for the developer including acceptance criteria")] string goal)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return "Error: Mode cannot be empty";
        }

        if (string.IsNullOrWhiteSpace(goal))
        {
            return "Error: Goal cannot be empty";
        }

        if (_state.CurrentTaskId == null || _state.CurrentSubtaskId == null)
        {
            return "Error: No subtask selected for assignment";
        }

        if (_developerConfig == null)
        {
            return "Error: Developer configuration not available";
        }

        (KanbanTaskDto? task, KanbanSubtaskDto? subtask) = FindTaskAndSubtask(_ticketHolder.Ticket, _state.CurrentTaskId, _state.CurrentSubtaskId);
        if (task == null || subtask == null)
        {
            return "Error: Could not find task or subtask";
        }

        try
        {
            TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _state.CurrentTaskId, _state.CurrentSubtaskId, SubtaskStatus.InProgress);
            if (updated != null)
            {
                _ticketHolder.Update(updated);
            }

            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer: Starting work on '{subtask.Name}'");
            _logger?.LogInformation("Developer: Working on '{Name}'", subtask.Name);

            string developerResult = await RunDeveloperAsync(task, subtask, goal, _developerConfig.Prompt);
            _state.Assigned = true;

            if (_state.DeveloperComplete)
            {
                updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _state.CurrentTaskId, _state.CurrentSubtaskId, SubtaskStatus.AwaitingReview);
                if (updated != null)
                {
                    _ticketHolder.Update(updated);
                }

                await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer: Completed work on '{subtask.Name}'");
                return $"Developer completed subtask. Status: {_state.DeveloperStatus}. Summary: {_state.DeveloperMessage}";
            }
            else
            {
                _logger?.LogWarning("Developer exceeded max iterations ({Max})", _developerConfig.MaxIterationsPerSubtask);
                await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, "Developer: Exceeded max iterations without completion");
                _state.Blocked = true;
                return "Developer exceeded max iterations without signaling completion. Subtask is blocked.";
            }
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while running developer";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to run developer: {ex.Message}";
        }
    }

    private async Task<string> RunDeveloperAsync(KanbanTaskDto task, KanbanSubtaskDto subtask, string goal, string developerPrompt)
    {
        string initialPrompt = $"""
            Complete this subtask:

            Task: {task.Name}
            Task Description: {task.Description}

            Subtask: {subtask.Name}
            Subtask Description: {subtask.Description}

            Goal: {goal}

            Call mark_subtask_complete when done or blocked.
            """;

        List<IToolProvider> toolProviders = _developerConfig!.ToolProvidersFactory();
        int iterations = 0;
        int stuckPromptingEvery = _developerConfig.StuckPromptingEvery > 0 ? _developerConfig.StuckPromptingEvery : 10;

        _state.Clear();

        LlmConversation conversation = _developerConfig.LlmProxy.CreateConversation(developerPrompt, initialPrompt);

        while (!_state.DeveloperComplete && iterations < _developerConfig.MaxIterationsPerSubtask)
        {
            iterations++;

            LlmResult result = await _developerConfig.LlmProxy.ContinueAsync(conversation, toolProviders, LlmRole.Developer, CancellationToken.None);
            _logger?.LogDebug("Developer response ({Iteration}): {Response}", iterations, result.Content.Length > 200 ? result.Content.Substring(0, 200) : result.Content);

            if (result.AccumulatedCost > 0)
            {
                await _apiClient.AddLlmCostAsync(_ticketHolder.Ticket.Id, result.AccumulatedCost);
            }

            if (!_state.DeveloperComplete)
            {
                if (iterations % stuckPromptingEvery == 0)
                {
                    _logger?.LogInformation("Developer iteration {Iteration}: prompting for stuck check", iterations);
                    await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer: Iteration {iterations} - checking progress");

                    conversation.AddUserMessage($"""
                        You have been working for {iterations} iterations without completing.

                        Take a moment to assess:
                        - Are you making progress toward the goal?
                        - Are you stuck in a loop trying the same approaches repeatedly?
                        - Have build or test errors remained unchanged despite your fixes?

                        If you are stuck, call mark_subtask_complete with status "blocked" and explain what you've tried.
                        If you are making progress, continue working and call mark_subtask_complete when done.
                        """);
                }
                else
                {
                    conversation.AddUserMessage("Continue working. Call mark_subtask_complete when done or blocked.");
                }
            }
        }

        await _developerConfig.LlmProxy.FinalizeConversationAsync(conversation, CancellationToken.None);

        return _state.DeveloperMessage;
    }

    private (KanbanTaskDto?, KanbanSubtaskDto?) FindTaskAndSubtask(TicketDto ticket, string taskId, string subtaskId)
    {
        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            if (task.Id == taskId)
            {
                foreach (KanbanSubtaskDto subtask in task.Subtasks)
                {
                    if (subtask.Id == subtaskId)
                    {
                        return (task, subtask);
                    }
                }
            }
        }

        return (null, null);
    }

    [Description("Call when the developer has completed the subtask or is blocked.")]
    public async Task<string> MarkSubtaskCompleteAsync(
        [Description("Status: 'done', 'blocked', or 'partial'")] string status,
        [Description("Summary of what was done")] string message)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return "Error: Status cannot be empty";
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return "Error: Message cannot be empty";
        }

        try
        {
            _state.DeveloperStatus = status.ToLowerInvariant() switch
            {
                "done" or "complete" => SubtaskCompleteStatus.Done,
                "blocked" => SubtaskCompleteStatus.Blocked,
                _ => SubtaskCompleteStatus.Partial
            };

            _state.DeveloperMessage = message;
            _state.DeveloperComplete = true;

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer: {status} - {message}");

            return $"Subtask marked as {status}";
        }
        catch (TaskCanceledException)
        {
            _state.DeveloperComplete = true;
            return $"Warning: Logged locally but API timed out. Subtask marked as {status}";
        }
        catch (Exception ex)
        {
            _state.DeveloperComplete = true;
            return $"Warning: Logged locally but API failed: {ex.Message}. Subtask marked as {status}";
        }
    }

    [Description("Approve the subtask - the work meets acceptance criteria.")]
    public async Task<string> ApproveSubtaskAsync(
        [Description("Notes about the approval explaining why it meets criteria")] string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return "Error: Notes cannot be empty";
        }

        if (_state.CurrentTaskId == null || _state.CurrentSubtaskId == null)
        {
            return "Error: No subtask selected for approval";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _state.CurrentTaskId, _state.CurrentSubtaskId, SubtaskStatus.Complete);

            if (updated == null)
            {
                return "Error: API returned null when approving subtask";
            }

            _ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask approved: {notes}");

            _state.SubtaskApproved = true;
            return "Subtask approved";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while approving subtask";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to approve subtask: {ex.Message}";
        }
    }

    [Description("Reject the subtask - the work does not meet acceptance criteria.")]
    public async Task<string> RejectSubtaskAsync(
        [Description("Reason for rejection - be specific about what needs to be fixed")] string reason)
    {
        if (_state.CurrentTaskId == null || _state.CurrentSubtaskId == null)
        {
            return "Error: No subtask selected for rejection";
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Error: Rejection reason cannot be empty";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _state.CurrentTaskId, _state.CurrentSubtaskId, SubtaskStatus.Rejected);

            if (updated == null)
            {
                return "Error: API returned null when rejecting subtask";
            }

            _ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask rejected: {reason}");

            _state.SubtaskApproved = false;
            _state.RejectionReason = reason;
            return $"Subtask rejected: {reason}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while rejecting subtask";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to reject subtask: {ex.Message}";
        }
    }

    [Description("Mark the ticket as complete - all work is done and verified.")]
    public async Task<string> MarkTicketCompleteAsync(
        [Description("Summary of what was accomplished")] string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return "Error: Summary cannot be empty";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            TicketDto? updated = await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Done");

            if (updated == null)
            {
                return "Error: API returned null when marking ticket complete";
            }

            _ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Ticket complete: {summary}");

            _state.TicketComplete = true;
            return "Ticket marked complete";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while marking ticket complete";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to mark ticket complete: {ex.Message}";
        }
    }

    [Description("Mark the ticket as blocked - requires human intervention.")]
    public async Task<string> MarkTicketBlockedAsync(
        [Description("Reason the ticket is blocked")] string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return "Error: Blocked reason cannot be empty";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            TicketDto? updated = await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed");

            if (updated == null)
            {
                return "Error: API returned null when marking ticket blocked";
            }

            _ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Ticket blocked: {reason}");

            _state.TicketComplete = false;
            _state.BlockedReason = reason;
            return $"Ticket blocked: {reason}";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while marking ticket blocked";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to mark ticket blocked: {ex.Message}";
        }
    }
}
