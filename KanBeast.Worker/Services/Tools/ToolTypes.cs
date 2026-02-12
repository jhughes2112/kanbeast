using System.Text.Json.Nodes;
using KanBeast.Worker.Services;

namespace KanBeast.Worker.Services.Tools;

// Roles that can be assigned to an LLM agent.
public enum LlmRole
{
	Planning,
	QA,
	Developer,
	Compaction,
	SubAgent
}

// Per-conversation state for tool calls.
public class ToolContext
{
	public HashSet<string> ReadFiles { get; }
	public LlmConversation? CompactionTarget { get; }
	public string? CurrentTaskId { get; }
	public string? CurrentSubtaskId { get; }
	public ShellState? Shell { get; internal set; }
	public LlmMemories Memories { get; }

	public ToolContext(
		LlmConversation? compactionTarget,
		string? currentTaskId,
		string? currentSubtaskId,
		LlmMemories memories)
	{
		ReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		CompactionTarget = compactionTarget;
		CurrentTaskId = currentTaskId;
		CurrentSubtaskId = currentSubtaskId;
		Memories = memories;
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

	public ToolResult(string response, bool exitLoop)
	{
		Response = response;
		ExitLoop = exitLoop;
	}
}

// A tool with its definition and invocation handler.
public class Tool
{
	public required ToolDefinition Definition { get; init; }
	public required Func<JsonObject, ToolContext, Task<ToolResult>> Handler { get; init; }
}
