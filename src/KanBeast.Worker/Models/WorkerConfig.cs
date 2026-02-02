namespace KanBeast.Worker.Models;

public class WorkerConfig
{
    public required string TicketId { get; set; }
    public required string ServerUrl { get; set; }
    public required GitConfig GitConfig { get; set; }
    public required List<LLMConfig> LLMConfigs { get; set; }
    public required string ManagerPrompt { get; set; }
    public required string DeveloperPrompt { get; set; }
    public required string PromptDirectory { get; set; }
}

public class LLMConfig
{
    public required string Name { get; set; }
    public required string Provider { get; set; }
    public required string ApiKey { get; set; }
    public required string Model { get; set; }
    public string? Endpoint { get; set; }
    public int Priority { get; set; }
    public bool IsEnabled { get; set; }
}

public class GitConfig
{
    public required string RepositoryUrl { get; set; }
    public string? SshKey { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
}

public class PromptTemplate
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}
