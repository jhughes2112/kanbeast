using System.ComponentModel;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to interact with the ticket system: logging, subtasks, status updates.
public class TicketTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IKanbanApiClient _apiClient;
    private readonly TicketHolder _ticketHolder;
    private readonly TaskState _state;
    private readonly Dictionary<LlmRole, List<Tool>> _toolsByRole;

    public TicketTools(IKanbanApiClient apiClient, TicketHolder ticketHolder, TaskState state)
    {
        _apiClient = apiClient;
        _ticketHolder = ticketHolder;
        _state = state;
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

    [Description("Create a subtask for an existing task.")]
    public async Task<string> CreateSubtaskAsync(
        [Description("Name of the task to add the subtask to")] string taskName,
        [Description("Name of the subtask")] string subtaskName,
        [Description("Description of the subtask")] string subtaskDescription)
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

    [Description("Assign the current subtask to the developer with specific instructions.")]
    public async Task<string> AssignSubtaskToDeveloperAsync(
        [Description("Mode: 'implementation', 'testing', or 'write-tests'")] string mode,
        [Description("Clear goal statement for the developer")] string goal)
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

        try
        {
            _state.AssignedMode = mode.ToLowerInvariant() switch
            {
                "testing" => DeveloperMode.Testing,
                "write-tests" or "writetests" => DeveloperMode.WriteTests,
                _ => DeveloperMode.Implementation
            };

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);

            TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _state.CurrentTaskId, _state.CurrentSubtaskId, SubtaskStatus.InProgress);
            if (updated != null)
            {
                _ticketHolder.Update(updated);
            }

            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Assigned to developer: {goal}");
            _state.Assigned = true;

            return $"Subtask assigned to developer in {_state.AssignedMode} mode";
        }
        catch (TaskCanceledException)
        {
            return "Error: Request timed out while assigning to developer";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to assign to developer: {ex.Message}";
        }
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
            TicketDto? updated = await _apiClient.UpdateSubtaskRejectionAsync(_ticketHolder.Ticket.Id, _state.CurrentTaskId, _state.CurrentSubtaskId, reason);

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
            TicketDto? updated = await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Blocked");

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
