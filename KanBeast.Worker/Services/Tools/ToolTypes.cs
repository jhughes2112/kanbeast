using System.Text.Json.Nodes;
using KanBeast.Worker.Services;

namespace KanBeast.Worker.Services.Tools;

// Roles that can be assigned to an LLM agent.
public enum LlmRole
{
	Planning,
	QA,
	Developer,
	Compaction
}

// Carries per-conversation dependencies into every tool call.
public class ToolContext
{
	public CancellationToken CancellationToken { get; }
	public IKanbanApiClient? ApiClient { get; }
	public TicketHolder? TicketHolder { get; }
	public string WorkDir { get; }
	public HashSet<string> ReadFiles { get; }
	public LlmConversation? CompactionTarget { get; }
	public string? CurrentTaskId { get; }
	public string? CurrentSubtaskId { get; }
	public ShellState? Shell { get; internal set; }

	public ToolContext(
		IKanbanApiClient? apiClient,
		TicketHolder? ticketHolder,
		string workDir,
		LlmConversation? compactionTarget,
		string? currentTaskId,
		string? currentSubtaskId,
		ShellState? shell,
		CancellationToken cancellationToken)
	{
		ApiClient = apiClient;
		TicketHolder = ticketHolder;
		WorkDir = workDir;
		ReadFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		CompactionTarget = compactionTarget;
		CurrentTaskId = currentTaskId;
		CurrentSubtaskId = currentSubtaskId;
		Shell = shell;
		CancellationToken = cancellationToken;
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
	public string? ToolName { get; init; }

	public ToolResult(string response, bool exitLoop = false, string? toolName = null)
	{
		Response = response;
		ExitLoop = exitLoop;
		ToolName = toolName;
	}
}

// A tool with its definition and invocation handler.
public class Tool
{
	public required ToolDefinition Definition { get; init; }
	public required Func<JsonObject, ToolContext, Task<ToolResult>> Handler { get; init; }
}
