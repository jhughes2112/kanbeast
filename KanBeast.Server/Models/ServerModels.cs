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

// Aggregates runtime settings for the API and UI.
public class Settings
{
    public SettingsFile File { get; set; } = new();
    public List<PromptTemplate> SystemPrompts { get; set; } = new();
}
