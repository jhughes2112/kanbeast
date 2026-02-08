namespace KanBeast.Worker.Models;

// Holds resolved runtime configuration for the worker process.
public class WorkerConfig
{
    public required string TicketId { get; set; }
    public required string ServerUrl { get; set; }
    public required GitConfig GitConfig { get; set; }
    public required List<LLMConfig> LLMConfigs { get; set; }
    public required CompactionSettings Compaction { get; set; }
    public required Dictionary<string, string> Prompts { get; set; }
    public required string PromptDirectory { get; set; }
    public bool JsonLogging { get; set; }

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
    public double Temperature { get; set; } = 0.2;
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
    public CompactionSettings Compaction { get; set; } = new();
    public bool JsonLogging { get; set; }
}

public class CompactionSettings
{
    public string Type { get; set; } = "summarize";
    public double ContextSizePercent { get; set; } = 0.9;
}
