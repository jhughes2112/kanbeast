namespace KanBeast.Worker.Models;

public class TicketDto
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Status { get; set; }
    public string? BranchName { get; set; }
    public List<KanbanTaskDto> Tasks { get; set; } = new List<KanbanTaskDto>();
    public decimal LlmCost { get; set; } = 0m;
    public decimal MaxCost { get; set; } = 0m;

    // Returns true if there is at least one task and all tasks have at least one subtask.
    public bool HasValidPlan()
    {
        if (Tasks.Count == 0)
        {
            return false;
        }

        foreach (KanbanTaskDto task in Tasks)
        {
            if (task.Subtasks.Count == 0)
            {
                return false;
            }
        }

        return true;
    }

    // Returns all subtasks that need work (Incomplete, InProgress, or Rejected).
    public List<(string TaskId, string TaskName, string SubtaskId, string SubtaskName, string SubtaskDescription)> GetIncompleteSubtasks()
    {
        List<(string, string, string, string, string)> result = new List<(string, string, string, string, string)>();

        foreach (KanbanTaskDto task in Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                if (subtask.Status == SubtaskStatus.Incomplete ||
                    subtask.Status == SubtaskStatus.InProgress ||
                    subtask.Status == SubtaskStatus.Rejected)
                {
                    result.Add((task.Id, task.Name, subtask.Id, subtask.Name, subtask.Description));
                }
            }
        }

        return result;
    }
}

public class KanbanTaskDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public List<KanbanSubtaskDto> Subtasks { get; set; } = new List<KanbanSubtaskDto>();
}

public class KanbanSubtaskDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public SubtaskStatus Status { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

// For creating new tasks/subtasks (without server-assigned fields)
public class KanbanTask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<KanbanSubtask> Subtasks { get; set; } = new List<KanbanSubtask>();
}

public class KanbanSubtask
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SubtaskStatus Status { get; set; } = SubtaskStatus.Incomplete;
}

public enum SubtaskStatus
{
    Incomplete,
    InProgress,
    AwaitingReview,
    Complete,
    Rejected
}
