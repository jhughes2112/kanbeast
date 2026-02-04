namespace KanBeast.Worker.Services;

// Tracks signals from tool invocations for orchestrator phase transitions.
public class TaskState
{
    // Breakdown phase
    public bool SubtasksCreated { get; set; }
    public int SubtaskCount { get; set; }

    // Assign phase
    public bool Assigned { get; set; }
    public string AssignedMode { get; set; } = DeveloperMode.Implementation;

    // Develop phase
    public bool DeveloperComplete { get; set; }
    public string DeveloperStatus { get; set; } = string.Empty;
    public string DeveloperMessage { get; set; } = string.Empty;

    // Verify phase
    public bool? SubtaskApproved { get; set; }
    public string? RejectionReason { get; set; }

    // Finalize phase
    public bool? TicketComplete { get; set; }
    public string? BlockedReason { get; set; }

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
        AssignedMode = DeveloperMode.Implementation;
        DeveloperComplete = false;
        DeveloperStatus = string.Empty;
        DeveloperMessage = string.Empty;
        SubtaskApproved = null;
        RejectionReason = null;
        TicketComplete = null;
        BlockedReason = null;
    }
}

public static class DeveloperMode
{
    public const string Implementation = "implementation";
    public const string Testing = "testing";
    public const string WriteTests = "write-tests";
}

public static class SubtaskCompleteStatus
{
    public const string Done = "done";
    public const string Blocked = "blocked";
    public const string Partial = "partial";
}
