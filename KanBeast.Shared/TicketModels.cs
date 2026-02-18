using System.Text;

namespace KanBeast.Shared;

public enum TicketStatus
{
	Backlog,
	Active,
	Failed,
	Done
}

public enum SubtaskStatus
{
	Incomplete,
	InProgress,
	AwaitingReview,
	Complete,
	Rejected
}

public class Ticket
{
	public string Id { get; set; } = string.Empty;
	public string Title { get; set; } = string.Empty;
	public string Description { get; set; } = string.Empty;
	public TicketStatus Status { get; set; } = TicketStatus.Backlog;
	public string? BranchName { get; set; }
	public string? PlannerLlmId { get; set; }
	public List<KanbanTask> Tasks { get; set; } = new();
	public List<string> ActivityLog { get; set; } = new();
	public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
	public DateTime? UpdatedAt { get; set; }
	public string? ContainerName { get; set; }
	public decimal LlmCost { get; set; } = 0m;
	public decimal MaxCost { get; set; } = 0m;
	public List<ConversationInfo> Conversations { get; set; } = new();

	public string FormatPlanningGoal()
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"Ticket: {Title}");
		sb.AppendLine($"Description: {Description}");

		if (Tasks.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Tasks:");
			foreach (KanbanTask task in Tasks)
			{
				sb.AppendLine($"  - {task.Name}");
				foreach (KanbanSubtask subtask in task.Subtasks)
				{
					sb.AppendLine($"      [{subtask.Status}] {subtask.Name}");
				}
			}
		}

		return sb.ToString().TrimEnd();
	}
}

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
