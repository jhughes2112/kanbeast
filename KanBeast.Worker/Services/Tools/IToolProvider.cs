using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Roles that can be assigned to an LLM agent.
public enum LlmRole
{
    Manager,
    Developer,
    Compaction
}

// A tool with its definition and invocation handler.
public class Tool
{
    public required ToolDefinition Definition { get; init; }
    public required Func<JsonObject, Task<string>> Handler { get; init; }
}

// Interface for classes that provide tools to LLM agents.
public interface IToolProvider
{
    void AddTools(List<Tool> tools, LlmRole role);
}
