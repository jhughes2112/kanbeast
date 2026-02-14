using KanBeast.Shared;

namespace KanBeast.Server.Models;

// Represents a prompt template used by the server UI.
public class PromptTemplate
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

// Defines settings persisted in env/settings.json.
public class SettingsFile
{
    public List<LLMConfig> LLMConfigs { get; set; } = new();
    public GitConfig GitConfig { get; set; } = new();
    public CompactionSettings Compaction { get; set; } = new();
    public WebSearchConfig WebSearch { get; set; } = new();
}

// Configures web search provider for agents.
public class WebSearchConfig
{
    public string Provider { get; set; } = "duckduckgo";
    public string? GoogleApiKey { get; set; }
    public string? GoogleSearchEngineId { get; set; }
}

// Aggregates runtime settings for the API and UI.
public class Settings
{
    public List<LLMConfig> LLMConfigs { get; set; } = new();
    public GitConfig GitConfig { get; set; } = new();
    public CompactionSettings Compaction { get; set; } = new();
    public WebSearchConfig WebSearch { get; set; } = new();
    public List<PromptTemplate> SystemPrompts { get; set; } = new();
}
