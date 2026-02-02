namespace KanBeast.Worker.Models;

public class TicketDto
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Status { get; set; }
    public string? BranchName { get; set; }
    public List<KanbanTaskDto> Tasks { get; set; } = new();
}

public class KanbanTaskDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public DateTime LastUpdatedAt { get; set; }
    public List<KanbanSubtaskDto> Subtasks { get; set; } = new();
}

public class KanbanSubtaskDto
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public SubtaskStatus Status { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}

public enum SubtaskStatus
{
    Incomplete,
    Complete
}
