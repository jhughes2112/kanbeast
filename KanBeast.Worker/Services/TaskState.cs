namespace KanBeast.Worker.Services;

// Tracks signals from tool invocations for orchestrator phase transitions.
public class TaskState
{
    // Planning phase
    public bool SubtasksCreated { get; set; }
    public int SubtaskCount { get; set; }

    // Developer communication
    public string? DeveloperResponse { get; set; }
    public bool HasDeveloperResponse { get; set; }

    // Ticket status
    public bool? TicketComplete { get; set; }
    public string? BlockedReason { get; set; }
    public bool Blocked { get; set; }

    // Current context
    public string? CurrentTaskId { get; set; }
    public string? CurrentSubtaskId { get; set; }

    public void SetCurrentSubtask(string taskId, string subtaskId)
    {
        CurrentTaskId = taskId;
        CurrentSubtaskId = subtaskId;
    }

    public void ClearDeveloperResponse()
    {
        DeveloperResponse = null;
        HasDeveloperResponse = false;
    }

    public void Clear()
    {
        SubtasksCreated = false;
        SubtaskCount = 0;
        DeveloperResponse = null;
        HasDeveloperResponse = false;
        TicketComplete = null;
        BlockedReason = null;
        Blocked = false;
    }
}
