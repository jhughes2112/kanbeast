namespace KanBeast.Worker.Models;

// Tracks the current state of agent orchestration
public class OrchestratorState
{
    public AgentRole CurrentAgent { get; set; } = AgentRole.Manager;
    public string? CurrentTaskId { get; set; }
    public string? CurrentSubtaskId { get; set; }
    public string? CurrentDeveloperMode { get; set; }
    public AssignToDeveloperParams? LastManagerAssignment { get; set; }
    public SubtaskCompleteParams? LastDeveloperResult { get; set; }
    public Dictionary<string, int> RejectionCounts { get; set; } = new Dictionary<string, int>();

    public int GetRejectionCount(string subtaskId)
    {
        if (RejectionCounts.TryGetValue(subtaskId, out int count))
        {
            return count;
        }

        return 0;
    }

    public void IncrementRejectionCount(string subtaskId)
    {
        if (RejectionCounts.TryGetValue(subtaskId, out int count))
        {
            RejectionCounts[subtaskId] = count + 1;
        }
        else
        {
            RejectionCounts[subtaskId] = 1;
        }
    }

    public void ResetRejectionCount(string subtaskId)
    {
        if (RejectionCounts.ContainsKey(subtaskId))
        {
            RejectionCounts.Remove(subtaskId);
        }
    }
}

public enum AgentRole
{
    Manager,
    Developer
}

// Result of a tool invocation that signals a state transition
public class AgentToolResult
{
    public required string ToolName { get; set; }
    public bool ShouldTransition { get; set; }
    public AgentRole? NextAgent { get; set; }
    public string? NextDeveloperMode { get; set; }
    public bool IsTerminal { get; set; }
    public string? Message { get; set; }
}

public static class AgentToolNames
{
    public const string AssignToDeveloper = "assign_to_developer";
    public const string UpdateSubtask = "update_subtask";
    public const string CompleteTicket = "complete_ticket";
    public const string SubtaskComplete = "subtask_complete";
}
