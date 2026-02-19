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
public class ToolContext
{
	public ConcurrentDictionary<string, byte> ReadFiles { get; }
	public string? CurrentTaskId { get; }
	public string? CurrentSubtaskId { get; }
	public string? SubAgentLlmConfigId { get; }

	// Set by LlmProxy.ContinueAsync so compaction and sub-conversations know which LLM to use.
	public string? LlmConfigId { get; internal set; }

	public ShellState? Shell { get; internal set; }

	// AsyncLocal-backed tool call ID so concurrent tool invocations each see their own value.
	// Set by LlmService before each tool handler runs.
	private static readonly AsyncLocal<string?> _activeToolCallId = new AsyncLocal<string?>();

	public static string? ActiveToolCallId
	{
		get => _activeToolCallId.Value;
		set => _activeToolCallId.Value = value;
	}

	public ToolContext(
		string? currentTaskId,
		string? currentSubtaskId,
		string? llmConfigId,
		string? subAgentLlmConfigId)
	{
		ReadFiles = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
		CurrentTaskId = currentTaskId;
		CurrentSubtaskId = currentSubtaskId;
		LlmConfigId = llmConfigId;
		SubAgentLlmConfigId = subAgentLlmConfigId;
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

// A tool with its definition and invocation handler.
public class Tool
{
	public required ToolDefinition Definition { get; init; }
	public required Func<JsonObject, ToolContext, Task<ToolResult>> Handler { get; init; }
}
