using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Roles that can be assigned to an LLM agent.
public enum LlmRole
{
    Planning,
    QA,
    Developer,
    Compaction
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
    public required Func<JsonObject, CancellationToken, Task<ToolResult>> Handler { get; init; }
}

// Interface for classes that provide tools to LLM agents.
public interface IToolProvider
{
    void AddTools(List<Tool> tools, LlmRole role);
}
