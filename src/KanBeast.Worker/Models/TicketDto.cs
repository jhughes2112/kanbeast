namespace KanBeast.Worker.Models;

public class TicketDto
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Status { get; set; }
    public string? BranchName { get; set; }
    public List<TaskDto> Tasks { get; set; } = new();
}

public class TaskDto
{
    public required string Id { get; set; }
    public required string Description { get; set; }
    public bool IsCompleted { get; set; }
}
