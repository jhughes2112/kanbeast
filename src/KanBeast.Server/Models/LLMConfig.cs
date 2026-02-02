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

public class SystemPrompts
{
    public string ManagerPrompt { get; set; } = @"You are a project manager responsible for breaking down feature requests into actionable tasks. Analyze the ticket and create a detailed task list that a developer can follow.";
    public string DeveloperPrompt { get; set; } = @"You are a software developer. Implement the assigned tasks by editing files and executing commands. Write clean, maintainable code following best practices.";
}

public class Settings
{
    public List<LLMConfig> LLMConfigs { get; set; } = new();
    public GitConfig GitConfig { get; set; } = new();
    public SystemPrompts SystemPrompts { get; set; } = new();
}
