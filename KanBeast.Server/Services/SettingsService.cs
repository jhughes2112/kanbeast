using System.Text.Json;
using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

// Defines operations for retrieving and persisting settings data.
public interface ISettingsService
{
    Task<Settings> GetSettingsAsync();
    Task<Settings> UpdateSettingsAsync(Settings settings);
    Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config);
    Task<bool> RemoveLLMConfigAsync(string id);
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
            throw new FileNotFoundException($"Settings file not found: {_settingsPath}. Create this file with valid configuration.", _settingsPath);
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
        Settings currentSettings = BuildSettings(settingsFile);

        // Merge incoming settings with current - only overwrite fields that were actually provided
        if (incomingSettings.LLMConfigs.Count > 0)
        {
            currentSettings.LLMConfigs = incomingSettings.LLMConfigs;
        }

        if (!string.IsNullOrEmpty(incomingSettings.GitConfig.RepositoryUrl) ||
            !string.IsNullOrEmpty(incomingSettings.GitConfig.Username) ||
            !string.IsNullOrEmpty(incomingSettings.GitConfig.Email))
        {
            currentSettings.GitConfig = incomingSettings.GitConfig;
        }

        if (!string.IsNullOrEmpty(incomingSettings.ManagerCompaction.Type))
        {
            currentSettings.ManagerCompaction = incomingSettings.ManagerCompaction;
        }

        if (!string.IsNullOrEmpty(incomingSettings.DeveloperCompaction.Type))
        {
            currentSettings.DeveloperCompaction = incomingSettings.DeveloperCompaction;
        }

        currentSettings.JsonLogging = incomingSettings.JsonLogging;

        if (incomingSettings.SystemPrompts.Count > 0)
        {
            currentSettings.SystemPrompts = UpdatePromptFiles(incomingSettings.SystemPrompts);
        }

        SettingsFile updatedFile = BuildSettingsFile(currentSettings);
        SaveSettingsFile(updatedFile);

        return Task.FromResult(currentSettings);
    }

    public Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config)
    {
        SettingsFile settingsFile = LoadSettingsFile();
        Settings settings = BuildSettings(settingsFile);
        settings.LLMConfigs.Add(config);

        SettingsFile updatedFile = BuildSettingsFile(settings);
        SaveSettingsFile(updatedFile);

        return Task.FromResult<LLMConfig?>(config);
    }

    public Task<bool> RemoveLLMConfigAsync(string id)
    {
        SettingsFile settingsFile = LoadSettingsFile();
        Settings settings = BuildSettings(settingsFile);

        LLMConfig? config = null;
        foreach (LLMConfig candidate in settings.LLMConfigs)
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

        settings.LLMConfigs.Remove(config);
        SettingsFile updatedFile = BuildSettingsFile(settings);
        SaveSettingsFile(updatedFile);

        return Task.FromResult(true);
    }

    private List<PromptTemplate> LoadPromptTemplates()
    {
        if (!Directory.Exists(_promptDirectory))
        {
            throw new DirectoryNotFoundException($"Prompt directory not found: {_promptDirectory}");
        }

        List<PromptTemplate> prompts = new List<PromptTemplate>
        {
            LoadPromptTemplate("manager", "Manager: System Prompt"),
            LoadPromptTemplate("developer", "Developer"),
            LoadPromptTemplate("manager-compaction-summary", "Manager: Compaction Summary Prompt"),
            LoadPromptTemplate("developer-compaction-summary", "Developer: Compaction Summary Prompt")
        };

        return prompts;
    }

    private PromptTemplate LoadPromptTemplate(string key, string displayName)
    {
        string fileName = $"{key}.txt";
        string content = LoadPromptFromDisk(_promptDirectory, key);

        return new PromptTemplate
        {
            Key = key,
            DisplayName = displayName,
            FileName = fileName,
            Content = content
        };
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

        if (string.IsNullOrEmpty(settings.ManagerCompaction.Type))
        {
            errors.Add("ManagerCompaction.Type is required");
        }

        if (settings.ManagerCompaction.ContextSizeThreshold <= 0)
        {
            errors.Add("ManagerCompaction.ContextSizeThreshold must be greater than 0");
        }

        if (string.IsNullOrEmpty(settings.DeveloperCompaction.Type))
        {
            errors.Add("DeveloperCompaction.Type is required");
        }

        if (settings.DeveloperCompaction.ContextSizeThreshold <= 0)
        {
            errors.Add("DeveloperCompaction.ContextSizeThreshold must be greater than 0");
        }

        if (string.IsNullOrEmpty(settings.WebSearch.Provider))
        {
            errors.Add("WebSearch.Provider is required");
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
            LLMConfigs = fileSettings.LLMConfigs,
            GitConfig = fileSettings.GitConfig,
            ManagerCompaction = fileSettings.ManagerCompaction,
            DeveloperCompaction = fileSettings.DeveloperCompaction,
            WebSearch = fileSettings.WebSearch,
            JsonLogging = fileSettings.JsonLogging,
            SystemPrompts = LoadPromptTemplates()
        };

        return settings;
    }

    private static SettingsFile BuildSettingsFile(Settings settings)
    {
        SettingsFile fileSettings = new SettingsFile
        {
            LLMConfigs = settings.LLMConfigs,
            GitConfig = settings.GitConfig,
            ManagerCompaction = settings.ManagerCompaction,
            DeveloperCompaction = settings.DeveloperCompaction,
            WebSearch = settings.WebSearch,
            JsonLogging = settings.JsonLogging
        };

        return fileSettings;
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
