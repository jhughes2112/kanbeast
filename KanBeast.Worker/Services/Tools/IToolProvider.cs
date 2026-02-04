using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Roles that can be assigned to an LLM agent.
public enum LlmRole
{
    Manager,
    Developer
}

// A tool exposed by a provider to the LLM.
public class ProviderTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required Func<JsonObject, Task<string>> InvokeAsync { get; init; }
}

// Interface for classes that provide tools to LLM agents.
public interface IToolProvider
{
    Dictionary<string, ProviderTool> GetTools(LlmRole role);
}
