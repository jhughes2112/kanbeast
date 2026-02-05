namespace KanBeast.Worker.Services;

// Tracks signals from tool invocations for orchestrator phase transitions.
public class TaskState
{
    // Breakdown phase
    public bool SubtasksCreated { get; set; }
    public int SubtaskCount { get; set; }

    // Assign phase
    public bool Assigned { get; set; }

    // Developer phase
    public bool DeveloperComplete { get; set; }
    public string DeveloperStatus { get; set; } = string.Empty;
    public string DeveloperMessage { get; set; } = string.Empty;

    // Verify phase
    public bool? SubtaskApproved { get; set; }
    public string? RejectionReason { get; set; }

    // Finalize phase
    public bool? TicketComplete { get; set; }
    public string? BlockedReason { get; set; }

    // General blocked state
    public bool Blocked { get; set; }

    // Current context
    public string? CurrentTaskId { get; set; }
    public string? CurrentSubtaskId { get; set; }

    public void SetCurrentSubtask(string taskId, string subtaskId)
    {
        CurrentTaskId = taskId;
        CurrentSubtaskId = subtaskId;
    }

    public void Clear()
    {
        SubtasksCreated = false;
        SubtaskCount = 0;
        Assigned = false;
        DeveloperComplete = false;
        DeveloperStatus = string.Empty;
        DeveloperMessage = string.Empty;
        SubtaskApproved = null;
        RejectionReason = null;
        TicketComplete = null;
        BlockedReason = null;
        Blocked = false;
    }
}

public static class SubtaskCompleteStatus
{
    public const string Done = "done";
    public const string Blocked = "blocked";
    public const string Partial = "partial";
}
