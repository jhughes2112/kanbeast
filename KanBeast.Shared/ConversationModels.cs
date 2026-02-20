using System.Text.Json.Serialization;

namespace KanBeast.Shared;

// A single message in a conversation, matching the LLM ChatMessage wire format exactly.
public class ConversationMessage
{
	[JsonPropertyName("role")]
	public string Role { get; set; } = string.Empty;

	[JsonPropertyName("content")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Content { get; set; }

	[JsonPropertyName("tool_calls")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<ConversationToolCall>? ToolCalls { get; set; }

	[JsonPropertyName("tool_call_id")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ToolCallId { get; set; }
}

public class ConversationToolCall
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("type")]
	public string Type { get; set; } = "function";

	[JsonPropertyName("function")]
	public ConversationFunctionCall Function { get; set; } = new();
}

public class ConversationFunctionCall
{
	[JsonPropertyName("name")]
	public string Name { get; set; } = string.Empty;

	[JsonPropertyName("arguments")]
	public string Arguments { get; set; } = string.Empty;
}

// Full conversation snapshot synced between worker and server.
public class ConversationData
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("displayName")]
	public string DisplayName { get; set; } = string.Empty;

	[JsonPropertyName("startedAt")]
	public string StartedAt { get; set; } = string.Empty;

	[JsonPropertyName("completedAt")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? CompletedAt { get; set; }

	[JsonPropertyName("messages")]
	public List<ConversationMessage> Messages { get; set; } = new();

	[JsonPropertyName("chapterSummaries")]
	public List<string> ChapterSummaries { get; set; } = new();

	[JsonPropertyName("isFinished")]
	public bool IsFinished { get; set; }

	[JsonPropertyName("role")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? Role { get; set; }

	[JsonPropertyName("activeModel")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ActiveModel { get; set; }
}

// Lightweight metadata returned in API responses.
public class ConversationInfo
{
	[JsonPropertyName("id")]
	public string Id { get; set; } = string.Empty;

	[JsonPropertyName("displayName")]
	public string DisplayName { get; set; } = string.Empty;

	[JsonPropertyName("messageCount")]
	public int MessageCount { get; set; }

	[JsonPropertyName("isFinished")]
	public bool IsFinished { get; set; }

	[JsonPropertyName("startedAt")]
	public string StartedAt { get; set; } = string.Empty;

	[JsonPropertyName("activeModel")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ActiveModel { get; set; }
}
