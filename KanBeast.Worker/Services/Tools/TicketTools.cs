using System.ComponentModel;
using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to interact with the ticket system: logging, subtasks, status updates.
public class TicketTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IKanbanApiClient _apiClient;
    private readonly TicketHolder _ticketHolder;
    private string? _currentTaskId;
    private string? _currentSubtaskId;

    public bool SubtasksCreated { get; private set; }
    public int SubtaskCount { get; private set; }
    public bool Assigned { get; private set; }
    public string AssignedMode { get; private set; } = DeveloperMode.Implementation;
    public bool? SubtaskApproved { get; private set; }
    public string? RejectionReason { get; private set; }
    public bool? TicketComplete { get; private set; }
    public string? BlockedReason { get; private set; }
    public bool DeveloperComplete { get; private set; }
    public string DeveloperStatus { get; private set; } = string.Empty;
    public string DeveloperMessage { get; private set; } = string.Empty;

    public TicketTools(IKanbanApiClient apiClient, TicketHolder ticketHolder)
    {
        _apiClient = apiClient;
        _ticketHolder = ticketHolder;
    }

    // Sets context for which subtask is currently being worked on.
    public void SetCurrentSubtask(string taskId, string subtaskId)
    {
        _currentTaskId = taskId;
        _currentSubtaskId = subtaskId;
    }

    // Resets all result flags for a new phase.
    public void ClearResults()
    {
        SubtasksCreated = false;
        SubtaskCount = 0;
        Assigned = false;
        AssignedMode = DeveloperMode.Implementation;
        SubtaskApproved = null;
        RejectionReason = null;
        TicketComplete = null;
        BlockedReason = null;
        DeveloperComplete = false;
        DeveloperStatus = string.Empty;
        DeveloperMessage = string.Empty;
    }

    [KernelFunction("log_message")]
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

    [KernelFunction("mark_task_complete")]
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

    [KernelFunction("create_task")]
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

    [KernelFunction("create_subtask")]
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

                        SubtasksCreated = true;
                        SubtaskCount += 1;

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

    [KernelFunction("assign_subtask_to_developer")]
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

        if (_currentTaskId == null || _currentSubtaskId == null)
        {
            return "Error: No subtask selected for assignment";
        }

        try
        {
            AssignedMode = mode.ToLowerInvariant() switch
            {
                "testing" => DeveloperMode.Testing,
                "write-tests" or "writetests" => DeveloperMode.WriteTests,
                _ => DeveloperMode.Implementation
            };

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);

            TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, SubtaskStatus.InProgress);
            if (updated != null)
            {
                _ticketHolder.Update(updated);
            }

            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Assigned to developer: {goal}");
            Assigned = true;

            return $"Subtask assigned to developer in {AssignedMode} mode";
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

    [KernelFunction("mark_subtask_complete")]
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
            DeveloperStatus = status.ToLowerInvariant() switch
            {
                "done" or "complete" => SubtaskCompleteStatus.Done,
                "blocked" => SubtaskCompleteStatus.Blocked,
                _ => SubtaskCompleteStatus.Partial
            };

            DeveloperMessage = message;
            DeveloperComplete = true;

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer: {status} - {message}");

            return $"Subtask marked as {status}";
        }
        catch (TaskCanceledException)
        {
            DeveloperComplete = true;
            return $"Warning: Logged locally but API timed out. Subtask marked as {status}";
        }
        catch (Exception ex)
        {
            DeveloperComplete = true;
            return $"Warning: Logged locally but API failed: {ex.Message}. Subtask marked as {status}";
        }
    }

    [KernelFunction("approve_subtask")]
    [Description("Approve the subtask - the work meets acceptance criteria.")]
    public async Task<string> ApproveSubtaskAsync(
        [Description("Notes about the approval explaining why it meets criteria")] string notes)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return "Error: Notes cannot be empty";
        }

        if (_currentTaskId == null || _currentSubtaskId == null)
        {
            return "Error: No subtask selected for approval";
        }

        try
        {
            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, SubtaskStatus.Complete);

            if (updated == null)
            {
                return "Error: API returned null when approving subtask";
            }

            _ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask approved: {notes}");

            SubtaskApproved = true;
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

    [KernelFunction("reject_subtask")]
    [Description("Reject the subtask - the work does not meet acceptance criteria.")]
    public async Task<string> RejectSubtaskAsync(
        [Description("Reason for rejection - be specific about what needs to be fixed")] string reason)
    {
        if (_currentTaskId == null || _currentSubtaskId == null)
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
            TicketDto? updated = await _apiClient.UpdateSubtaskRejectionAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, reason);

            if (updated == null)
            {
                return "Error: API returned null when rejecting subtask";
            }

            _ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask rejected: {reason}");

            SubtaskApproved = false;
            RejectionReason = reason;
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

    [KernelFunction("mark_ticket_complete")]
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

            TicketComplete = true;
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

    [KernelFunction("mark_ticket_blocked")]
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

            TicketComplete = false;
            BlockedReason = reason;
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
