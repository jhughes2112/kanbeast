using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Roles that can be assigned to an LLM agent.
public enum LlmRole
{
	Planning,
	PlanningActive,
	Developer,
	PlanningSubagent,
	DeveloperSubagent,
	Compaction
}

// Per-conversation state for tool calls.
// Built from a conversation to give tools access to services and execution state.
public class ToolContext
{
	public ConcurrentDictionary<string, byte> ReadFiles { get; }
	public ILlmConversation? Conversation { get; }
	public LlmService? LlmService { get; internal set; }
	public LlmService? SubAgentService { get; internal set; }

	// Conversation-scoped token set by LlmService.RunToCompletionAsync.
	// Tools should pass this to child RunToCompletionAsync calls for interrupt cascade.
	public CancellationToken CancellationToken { get; internal set; }

	public ShellState? Shell { get; internal set; }

	// AsyncLocal-backed tool call ID so concurrent tool invocations each see their own value.
	// Set by LlmService before each tool handler runs.
	private static readonly AsyncLocal<string?> _activeToolCallId = new AsyncLocal<string?>();

	public static string? ActiveToolCallId
	{
		get => _activeToolCallId.Value;
		set => _activeToolCallId.Value = value;
	}

	public ToolContext(ILlmConversation? conversation, LlmService? llmService, LlmService? subAgentService)
	{
		ReadFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		Conversation = conversation;
		LlmService = llmService;
		SubAgentService = subAgentService;
		Shell = null;
	}

	// Disposes and removes the persistent shell if one exists.
	public void DestroyShell()
	{
		if (Shell != null)
		{
			PersistentShellTools.Destroy(Shell);
			Shell = null;
		}
	}
}

// Result from a tool invocation.
public readonly struct ToolResult
{
	public string Response { get; init; }
	public bool ExitLoop { get; init; }

	// When true, the handler already managed conversation messages. LlmService skips AddToolMessageAsync.
	public bool MessageHandled { get; init; }

	public ToolResult(string response, bool exitLoop, bool messageHandled)
	{
		Response = response;
		ExitLoop = exitLoop;
		MessageHandled = messageHandled;
	}
}

// Marks a tool method as requiring sequential execution within a batch.
// When an LLM returns multiple tool calls in a single message, tools with
// this attribute run in their original order rather than in parallel.
[AttributeUsage(AttributeTargets.Method)]
public sealed class SequentialAttribute : Attribute { }

// Marks a tool method as potentially long-running.
// When a batch contains any slow tool call, the assistant message is flushed
// to the client before execution so the UI can show pending tool names.
[AttributeUsage(AttributeTargets.Method)]
public sealed class SlowCallAttribute : Attribute { }

// A tool with its definition and invocation handler.
public class Tool
{
	public required ToolDefinition Definition { get; init; }
	public required Func<JsonObject, ToolContext, Task<ToolResult>> Handler { get; init; }
	public bool MustRunSequentially { get; init; }
	public bool IsSlowCall { get; init; }
}
