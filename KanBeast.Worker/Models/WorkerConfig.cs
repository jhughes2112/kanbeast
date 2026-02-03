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
    public required string ManagerCompactionSummaryPrompt { get; set; }
    public required string ManagerCompactionSystemPrompt { get; set; }
    public required string DeveloperCompactionSummaryPrompt { get; set; }
    public required string DeveloperCompactionSystemPrompt { get; set; }
    public required string ManagerPrompt { get; set; }
    public required string DeveloperPrompt { get; set; }
    public required string DeveloperImplementationPrompt { get; set; }
    public required string DeveloperTestingPrompt { get; set; }
    public required string DeveloperWriteTestsPrompt { get; set; }
    public required string PromptDirectory { get; set; }
    public int MaxIterationsPerSubtask { get; set; } = 50;
}

// Describes a single LLM endpoint used by the worker.
public class LLMConfig
{
    public required string ApiKey { get; set; }
    public required string Model { get; set; }
    public string? Endpoint { get; set; }
    public int ContextLength { get; set; } = 128000;
}

// Stores Git integration settings for worker operations.
public class GitConfig
{
    public required string RepositoryUrl { get; set; }
    public string? SshKey { get; set; }
    public required string Username { get; set; }
    public required string Email { get; set; }
}

// Represents a prompt template loaded from disk.
public class PromptTemplate
{
    public string Key { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}

// Defines configuration values loaded from settings.json.
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

// Defines compaction behavior for an agent prompt context.
public class CompactionSettings
{
    public string Type { get; set; } = string.Empty;
    public int ContextSizeThreshold { get; set; }
}
