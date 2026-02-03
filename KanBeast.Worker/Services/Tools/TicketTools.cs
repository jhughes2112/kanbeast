using System.ComponentModel;
using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to interact with the ticket system: logging, subtasks, status updates.
public class TicketTools
{
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
        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, message);
        return "Message logged";
    }

    [KernelFunction("create_subtasks")]
    [Description("Create subtasks for the ticket. Call this after analyzing what needs to be done.")]
    public async Task<string> CreateSubtasksAsync(
        [Description("Name of the main task grouping these subtasks")] string taskName,
        [Description("Array of subtask names in order of execution")] string[] subtasks)
    {
        if (subtasks.Length == 0)
        {
            return "Error: No subtasks provided";
        }

        KanbanTask task = new KanbanTask
        {
            Name = taskName,
            Subtasks = new List<KanbanSubtask>()
        };

        foreach (string subtaskName in subtasks)
        {
            task.Subtasks.Add(new KanbanSubtask
            {
                Name = subtaskName,
                Status = SubtaskStatus.Incomplete
            });
        }

        TicketDto? updated = await _apiClient.AddTaskToTicketAsync(_ticketHolder.Ticket.Id, task);
        _ticketHolder.Update(updated);
        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created task '{taskName}' with {subtasks.Length} subtasks");

        SubtasksCreated = updated != null;
        SubtaskCount = subtasks.Length;

        return SubtasksCreated
            ? $"Created {subtasks.Length} subtasks under '{taskName}'"
            : "Error: Failed to create subtasks";
    }

    [KernelFunction("assign_to_developer")]
    [Description("Assign the current subtask to the developer with specific instructions.")]
    public async Task<string> AssignToDeveloperAsync(
        [Description("Mode: 'implementation', 'testing', or 'write-tests'")] string mode,
        [Description("Clear goal statement for the developer")] string goal)
    {
        AssignedMode = mode.ToLowerInvariant() switch
        {
            "testing" => DeveloperMode.Testing,
            "write-tests" or "writetests" => DeveloperMode.WriteTests,
            _ => DeveloperMode.Implementation
        };

        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Assigned to developer: {goal}");
        Assigned = true;

        return $"Subtask assigned to developer in {AssignedMode} mode";
    }

    [KernelFunction("subtask_complete")]
    [Description("Call when the developer has completed the subtask or is blocked.")]
    public async Task<string> SubtaskCompleteAsync(
        [Description("Status: 'done', 'blocked', or 'partial'")] string status,
        [Description("Summary of what was done")] string message)
    {
        DeveloperStatus = status.ToLowerInvariant() switch
        {
            "done" or "complete" => SubtaskCompleteStatus.Done,
            "blocked" => SubtaskCompleteStatus.Blocked,
            _ => SubtaskCompleteStatus.Partial
        };

        DeveloperMessage = message;
        DeveloperComplete = true;

        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer: {status} - {message}");
        return $"Subtask marked as {status}";
    }

    [KernelFunction("approve_subtask")]
    [Description("Approve the subtask - the work meets acceptance criteria.")]
    public async Task<string> ApproveSubtaskAsync(
        [Description("Optional notes about the approval")] string? notes = null)
    {
        if (_currentTaskId == null || _currentSubtaskId == null)
        {
            return "Error: No subtask selected";
        }

        TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, SubtaskStatus.Complete);
        _ticketHolder.Update(updated);
        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask approved{(notes != null ? $": {notes}" : "")}");

        SubtaskApproved = true;
        return "Subtask approved";
    }

    [KernelFunction("reject_subtask")]
    [Description("Reject the subtask - the work does not meet acceptance criteria.")]
    public async Task<string> RejectSubtaskAsync(
        [Description("Reason for rejection - be specific about what needs to be fixed")] string reason)
    {
        if (_currentTaskId == null || _currentSubtaskId == null)
        {
            return "Error: No subtask selected";
        }

        TicketDto? updated = await _apiClient.UpdateSubtaskRejectionAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, reason);
        _ticketHolder.Update(updated);
        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask rejected: {reason}");

        SubtaskApproved = false;
        RejectionReason = reason;
        return $"Subtask rejected: {reason}";
    }

    [KernelFunction("mark_complete")]
    [Description("Mark the ticket as complete - all work is done and verified.")]
    public async Task<string> MarkCompleteAsync(
        [Description("Summary of what was accomplished")] string summary)
    {
        TicketDto? updated = await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Done");
        _ticketHolder.Update(updated);
        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Ticket complete: {summary}");

        TicketComplete = true;
        return "Ticket marked complete";
    }

    [KernelFunction("mark_blocked")]
    [Description("Mark the ticket as blocked - requires human intervention.")]
    public async Task<string> MarkBlockedAsync(
        [Description("Reason the ticket is blocked")] string reason)
    {
        TicketDto? updated = await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Blocked");
        _ticketHolder.Update(updated);
        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Ticket blocked: {reason}");

        TicketComplete = false;
        BlockedReason = reason;
        return $"Ticket blocked: {reason}";
    }
}
