namespace KanBeast.Server.Models;

// Describes a single LLM endpoint available to the system.
public class LLMConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public int ContextLength { get; set; } = 128000;
    public decimal InputTokenPrice { get; set; } = 0m;
    public decimal OutputTokenPrice { get; set; } = 0m;
}

// Stores Git integration settings for workers.
public class GitConfig
{
    public string RepositoryUrl { get; set; } = string.Empty;
    public string? SshKey { get; set; }
    public string? Password { get; set; }
    public string? ApiToken { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

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
    public CompactionSettings ManagerCompaction { get; set; } = new();
    public CompactionSettings DeveloperCompaction { get; set; } = new();
    public WebSearchConfig WebSearch { get; set; } = new();
    public int MaxIterationsPerSubtask { get; set; } = 50;
    public int StuckPromptingEvery { get; set; } = 10;
    public bool JsonLogging { get; set; }
}

// Configures compaction behavior for agent context handling.
public class CompactionSettings
{
    public string Type { get; set; } = string.Empty;
    public int ContextSizeThreshold { get; set; }
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
    public int LlmRetryCount { get; set; }
    public int LlmRetryDelaySeconds { get; set; }
    public CompactionSettings ManagerCompaction { get; set; } = new();
    public CompactionSettings DeveloperCompaction { get; set; } = new();
    public WebSearchConfig WebSearch { get; set; } = new();
    public List<PromptTemplate> SystemPrompts { get; set; } = new();
    public int MaxIterationsPerSubtask { get; set; } = 50;
    public int StuckPromptingEvery { get; set; } = 10;
    public bool JsonLogging { get; set; }
}
