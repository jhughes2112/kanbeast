using System.Text.Json.Serialization;

namespace KanBeast.Server.Models;

public enum TicketStatus
{
    Backlog,
    Active,
    Failed,
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
    public string? ContainerName { get; set; }
    public decimal LlmCost { get; set; } = 0m;
    public decimal MaxCost { get; set; } = 0m;

    // Lightweight metadata for each conversation the worker has registered.
    public List<ConversationInfo> Conversations { get; set; } = new();
}

// Lightweight metadata stored on the ticket and returned in API responses.
public class ConversationInfo
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public bool IsFinished { get; set; }
}

// A single message in a stored conversation.
public class ConversationMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("toolCall")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCall { get; set; }

    [JsonPropertyName("toolResult")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolResult { get; set; }
}

// Full conversation data stored by ConversationStore.
public class ConversationData
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<ConversationMessage> Messages { get; set; } = new();
    public bool IsFinished { get; set; }
}
