namespace KanBeast.Server.Models;

public enum TicketStatus
{
    Backlog,
    Active,
    Testing,
    Done
}

public class Ticket
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public TicketStatus Status { get; set; } = TicketStatus.Backlog;
    public string? BranchName { get; set; }
    public List<KanbanTask> Tasks { get; set; } = new();
    public List<string> ActivityLog { get; set; } = new();
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
    public string? WorkerId { get; set; }
}
