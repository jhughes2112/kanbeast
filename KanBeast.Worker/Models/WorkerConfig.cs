namespace KanBeast.Worker.Models;

// Holds resolved runtime configuration for the worker process.
public class WorkerConfig
{
    public required string TicketId { get; set; }
    public required string ServerUrl { get; set; }
    public required GitConfig GitConfig { get; set; }
    public required List<LLMConfig> LLMConfigs { get; set; }
    public required int LlmRetryCount { get; set; }
    public required int LlmRetryDelaySeconds { get; set; }
    public required CompactionSettings ManagerCompaction { get; set; }
    public required CompactionSettings DeveloperCompaction { get; set; }
    public required Dictionary<string, string> Prompts { get; set; }
    public required string PromptDirectory { get; set; }
    public int MaxIterationsPerSubtask { get; set; } = 50;

    public string GetPrompt(string key)
    {
        if (Prompts.TryGetValue(key, out string? prompt))
        {
            return prompt;
        }
        return string.Empty;
    }
}

// Describes a single LLM endpoint used by the worker.
public class LLMConfig
{
    public required string ApiKey { get; set; }
    public required string Model { get; set; }
    public string? Endpoint { get; set; }
    public int ContextLength { get; set; } = 128000;
    public decimal InputTokenPrice { get; set; } = 0m;
    public decimal OutputTokenPrice { get; set; } = 0m;
}

// Stores Git integration settings for worker operations.
public class GitConfig
{
    public required string RepositoryUrl { get; set; }
    public string? SshKey { get; set; }
    public string? Password { get; set; }
    public string? ApiToken { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
}

public class WorkerSettings
{
    public List<LLMConfig> LLMConfigs { get; set; } = new();
    public GitConfig GitConfig { get; set; } = new()
    {
        RepositoryUrl = string.Empty,
        Username = string.Empty,
        Email = string.Empty
    };
    public int LlmRetryCount { get; set; }
    public int LlmRetryDelaySeconds { get; set; }
    public CompactionSettings ManagerCompaction { get; set; } = new();
    public CompactionSettings DeveloperCompaction { get; set; } = new();
}

public class CompactionSettings
{
    public string Type { get; set; } = string.Empty;
    public int ContextSizeThreshold { get; set; }
}
