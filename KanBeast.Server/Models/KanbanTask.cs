namespace KanBeast.Server.Models;

public class KanbanTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public List<KanbanSubtask> Subtasks { get; set; } = new();
}

public class KanbanSubtask
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public SubtaskStatus Status { get; set; } = SubtaskStatus.Incomplete;
    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum SubtaskStatus
{
    Incomplete,
    InProgress,
    Complete
}
