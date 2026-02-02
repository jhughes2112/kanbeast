namespace KanBeast.Server.Models;

public class LLMConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty; // e.g., "openai", "anthropic", "azure"
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string? Endpoint { get; set; }
    public int Priority { get; set; } // Lower number = higher priority
    public bool IsEnabled { get; set; } = true;
}

public class GitConfig
{
    public string RepositoryUrl { get; set; } = string.Empty;
    public string? SshKey { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class PromptTemplate
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

public class Settings
{
    public List<LLMConfig> LLMConfigs { get; set; } = new();
    public GitConfig GitConfig { get; set; } = new();
    public List<PromptTemplate> SystemPrompts { get; set; } = new();
}
