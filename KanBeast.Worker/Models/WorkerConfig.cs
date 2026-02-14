using KanBeast.Shared;

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

    public string GetPrompt(string key)
    {
        if (Prompts.TryGetValue(key, out string? prompt))
        {
            return prompt;
        }
        return string.Empty;
    }
}

public class WorkerSettings
{
    public List<LLMConfig> LLMConfigs { get; set; } = new();
    public GitConfig GitConfig { get; set; } = new();
    public CompactionSettings Compaction { get; set; } = new();
}
