using System.Text.Json;
using KanBeast.Server.Models;
using KanBeast.Shared;

namespace KanBeast.Server.Services;

// Defines operations for retrieving and persisting settings data.
public interface ISettingsService
{
    Task<Settings> GetSettingsAsync();
    Task<Settings> UpdateSettingsAsync(Settings settings);
    Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config);
    Task<bool> RemoveLLMConfigAsync(string id);
    Task<LLMConfig?> UpdateLlmNotesAsync(string id, string strengths, string weaknesses);
}

// Manages settings stored on disk and prompt files for the server.
public class SettingsService : ISettingsService
{
    private readonly string _promptDirectory;
    private readonly string _settingsPath;

    public SettingsService()
    {
        _promptDirectory = ResolvePromptDirectory();
        _settingsPath = ResolveSettingsPath();

        if (!File.Exists(_settingsPath))
        {
            Console.WriteLine($"Settings file not found at {_settingsPath}, creating default settings...");
            SettingsFile defaultSettings = new SettingsFile();
            SaveSettingsFile(defaultSettings);
            Console.WriteLine("Default settings file created. Please configure LLM and Git settings via the UI.");
        }
    }

    public Task<Settings> GetSettingsAsync()
    {
        SettingsFile settingsFile = LoadSettingsFile();
        Settings settings = BuildSettings(settingsFile);
        return Task.FromResult(settings);
    }

    public Task<Settings> UpdateSettingsAsync(Settings incomingSettings)
    {
        SettingsFile settingsFile = LoadSettingsFile();

        // Merge incoming settings with current - only overwrite fields that were actually provided
        if (incomingSettings.File.LLMConfigs.Count > 0)
        {
            settingsFile.LLMConfigs = incomingSettings.File.LLMConfigs;
        }

        if (!string.IsNullOrEmpty(incomingSettings.File.GitConfig.RepositoryUrl) ||
            !string.IsNullOrEmpty(incomingSettings.File.GitConfig.Username) ||
            !string.IsNullOrEmpty(incomingSettings.File.GitConfig.Email))
        {
            settingsFile.GitConfig = incomingSettings.File.GitConfig;
        }

        if (!string.IsNullOrEmpty(incomingSettings.File.Compaction.Type))
        {
            settingsFile.Compaction = incomingSettings.File.Compaction;
        }

        if (!string.IsNullOrEmpty(incomingSettings.File.WebSearch.Engine))
        {
            settingsFile.WebSearch = incomingSettings.File.WebSearch;
        }

        if (incomingSettings.SystemPrompts.Count > 0)
        {
            incomingSettings.SystemPrompts = UpdatePromptFiles(incomingSettings.SystemPrompts);
        }

        SaveSettingsFile(settingsFile);

        Settings result = BuildSettings(settingsFile);
        result.SystemPrompts = incomingSettings.SystemPrompts;
        return Task.FromResult(result);
    }

    public Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config)
    {
        SettingsFile settingsFile = LoadSettingsFile();
        settingsFile.LLMConfigs.Add(config);
        SaveSettingsFile(settingsFile);

        return Task.FromResult<LLMConfig?>(config);
    }

    public Task<bool> RemoveLLMConfigAsync(string id)
    {
        SettingsFile settingsFile = LoadSettingsFile();

        LLMConfig? config = null;
        foreach (LLMConfig candidate in settingsFile.LLMConfigs)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                config = candidate;
                break;
            }
        }

        if (config == null)
        {
            return Task.FromResult(false);
        }

        settingsFile.LLMConfigs.Remove(config);
        SaveSettingsFile(settingsFile);

        return Task.FromResult(true);
    }

    public Task<LLMConfig?> UpdateLlmNotesAsync(string id, string strengths, string weaknesses)
    {
        SettingsFile settingsFile = LoadSettingsFile();

        LLMConfig? config = null;
        foreach (LLMConfig candidate in settingsFile.LLMConfigs)
        {
            if (string.Equals(candidate.Id, id, StringComparison.Ordinal))
            {
                config = candidate;
                break;
            }
        }

        if (config == null)
        {
            return Task.FromResult<LLMConfig?>(null);
        }

        config.Strengths = strengths;
        config.Weaknesses = weaknesses;
        SaveSettingsFile(settingsFile);

        return Task.FromResult<LLMConfig?>(config);
    }

    private List<PromptTemplate> LoadPromptTemplates()
    {
        if (!Directory.Exists(_promptDirectory))
        {
            throw new DirectoryNotFoundException($"Prompt directory not found: {_promptDirectory}");
        }

        List<PromptTemplate> prompts = new List<PromptTemplate>();
        string[] promptFiles = Directory.GetFiles(_promptDirectory, "*.txt");

        foreach (string filePath in promptFiles)
        {
            string fileName = Path.GetFileName(filePath);
            string key = Path.GetFileNameWithoutExtension(filePath);
            string displayName = GenerateDisplayName(key);
            string content = File.ReadAllText(filePath);

            Console.WriteLine($"Loaded prompt: {key}");

            prompts.Add(new PromptTemplate
            {
                Key = key,
                DisplayName = displayName,
                FileName = fileName,
                Content = content
            });
        }

        return prompts;
    }

    private static string GenerateDisplayName(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        string[] words = key.Split(new[] { '_', '-' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (words[i].Length > 0)
            {
                words[i] = char.ToUpper(words[i][0]) + words[i].Substring(1);
            }
        }

        return string.Join(" ", words);
    }

    private List<PromptTemplate> UpdatePromptFiles(IEnumerable<PromptTemplate> prompts)
    {
        List<PromptTemplate> updatedPrompts = new List<PromptTemplate>();
        foreach (PromptTemplate prompt in prompts)
        {
            string fileName = string.IsNullOrWhiteSpace(prompt.FileName)
                ? $"{prompt.Key}.txt"
                : prompt.FileName;
            string path = Path.Combine(_promptDirectory, fileName);
            File.WriteAllText(path, prompt.Content ?? string.Empty);

            updatedPrompts.Add(new PromptTemplate
            {
                Key = prompt.Key,
                DisplayName = prompt.DisplayName,
                FileName = fileName,
                Content = prompt.Content ?? string.Empty
            });
        }

        return updatedPrompts;
    }

    private SettingsFile LoadSettingsFile()
    {
        string json = File.ReadAllText(_settingsPath);
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        SettingsFile? settingsFile = JsonSerializer.Deserialize<SettingsFile>(json, options);
        if (settingsFile == null)
        {
            throw new InvalidOperationException($"Failed to parse settings file: {_settingsPath}. File contains invalid JSON.");
        }

        ValidateSettingsFile(settingsFile);
        return settingsFile;
    }

    private void ValidateSettingsFile(SettingsFile settings)
    {
        List<string> errors = new List<string>();

        if (string.IsNullOrEmpty(settings.Compaction.Type))
        {
            errors.Add("Compaction.Type is required");
        }

        if (string.Equals(settings.Compaction.Type, "summarize", StringComparison.OrdinalIgnoreCase) &&
            (settings.Compaction.ContextSizePercent <= 0 || settings.Compaction.ContextSizePercent > 1))
        {
            errors.Add("Compaction.ContextSizePercent must be between 0 and 1");
        }

        if (string.IsNullOrEmpty(settings.WebSearch.Engine))
        {
            errors.Add("WebSearch.Engine is required");
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException($"Invalid settings in {_settingsPath}: {string.Join("; ", errors)}");
        }
    }

    private void SaveSettingsFile(SettingsFile settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? string.Empty);
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        string json = JsonSerializer.Serialize(settings, options);
        File.WriteAllText(_settingsPath, json);
    }

    private Settings BuildSettings(SettingsFile fileSettings)
    {
        Settings settings = new Settings
        {
            File = fileSettings,
            SystemPrompts = LoadPromptTemplates()
        };

        return settings;
    }

    private static string ResolvePromptDirectory()
    {
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "prompts"));
    }

    private static string ResolveSettingsPath()
    {
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "settings.json"));
    }

    private static string LoadPromptFromDisk(string promptDirectory, string promptName)
    {
        string filePath = Path.Combine(promptDirectory, $"{promptName}.txt");
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Required prompt file not found: {filePath}", filePath);
        }

        return File.ReadAllText(filePath);
    }
}
