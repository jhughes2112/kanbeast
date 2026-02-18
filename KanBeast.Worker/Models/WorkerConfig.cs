using KanBeast.Shared;

namespace KanBeast.Worker.Models;

// Holds resolved runtime configuration for the worker process.
public class WorkerConfig
{
    public required string TicketId { get; set; }
    public required string ServerUrl { get; set; }
    public required SettingsFile Settings { get; set; }
    public required Dictionary<string, string> Prompts { get; set; }
    public required string PromptDirectory { get; set; }

    public string GetPrompt(string key)
    {
        if (Prompts.TryGetValue(key, out string? prompt))
        {
            return prompt;
        }
        throw new KeyNotFoundException($"Required prompt not found: {key}");
    }
}
