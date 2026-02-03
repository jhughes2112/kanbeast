namespace KanBeast.Worker.Models;

public class TicketDto
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Status { get; set; }
    public string? BranchName { get; set; }
    public List<KanbanTaskDto> Tasks { get; set; } = new List<KanbanTaskDto>();
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
    public int RejectionCount { get; set; }
    public string? LastRejectionNotes { get; set; }
    public List<string> FilesToInspect { get; set; } = new List<string>();
    public List<string> FilesToModify { get; set; } = new List<string>();
    public List<string> AcceptanceCriteria { get; set; } = new List<string>();
    public List<string> Constraints { get; set; } = new List<string>();
}

public enum SubtaskStatus
{
    Incomplete,
    InProgress,
    AwaitingReview,
    Complete,
    Rejected,
    Blocked
}
