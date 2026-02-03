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
    private Settings _settings;

    public SettingsService(IWebHostEnvironment environment)
    {
        _promptDirectory = Path.Combine(environment.ContentRootPath, "env", "prompts");
        _settingsPath = Path.Combine(environment.ContentRootPath, "env", "settings.json");
        Directory.CreateDirectory(_promptDirectory);
        SettingsFile settingsFile = LoadSettingsFile();
        _settings = BuildSettings(settingsFile);
    }

    public Task<Settings> GetSettingsAsync()
    {
        SettingsFile settingsFile = LoadSettingsFile();
        _settings = BuildSettings(settingsFile);
        return Task.FromResult(_settings);
    }

    public Task<Settings> UpdateSettingsAsync(Settings settings)
    {
        // Merge incoming settings with existing - only overwrite fields that were actually provided
        if (settings.LLMConfigs.Count > 0)
        {
            _settings.LLMConfigs = settings.LLMConfigs;
        }

        if (!string.IsNullOrEmpty(settings.GitConfig.RepositoryUrl) ||
            !string.IsNullOrEmpty(settings.GitConfig.Username) ||
            !string.IsNullOrEmpty(settings.GitConfig.Email))
        {
            _settings.GitConfig = settings.GitConfig;
        }

        if (settings.LlmRetryCount > 0)
        {
            _settings.LlmRetryCount = settings.LlmRetryCount;
        }

        if (settings.LlmRetryDelaySeconds > 0)
        {
            _settings.LlmRetryDelaySeconds = settings.LlmRetryDelaySeconds;
        }

        if (!string.IsNullOrEmpty(settings.ManagerCompaction.Type))
        {
            _settings.ManagerCompaction = settings.ManagerCompaction;
        }

        if (!string.IsNullOrEmpty(settings.DeveloperCompaction.Type))
        {
            _settings.DeveloperCompaction = settings.DeveloperCompaction;
        }

        if (settings.SystemPrompts.Count > 0)
        {
            _settings.SystemPrompts = UpdatePromptFiles(settings.SystemPrompts);
        }

        SettingsFile fileSettings = BuildSettingsFile(_settings);
        SaveSettingsFile(fileSettings);

        return Task.FromResult(_settings);
    }

    public Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config)
    {
        _settings.LLMConfigs.Add(config);
        SettingsFile fileSettings = BuildSettingsFile(_settings);
        SaveSettingsFile(fileSettings);

        return Task.FromResult<LLMConfig?>(config);
    }

    public Task<bool> RemoveLLMConfigAsync(string id)
    {
        LLMConfig? config = null;
        foreach (LLMConfig candidate in _settings.LLMConfigs)
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

        _settings.LLMConfigs.Remove(config);
        SettingsFile fileSettings = BuildSettingsFile(_settings);
        SaveSettingsFile(fileSettings);

        return Task.FromResult(true);
    }

    private List<PromptTemplate> LoadPromptTemplates()
    {
        List<PromptTemplate> prompts = GetDefaultPrompts();
        foreach (PromptTemplate prompt in prompts)
        {
            string path = Path.Combine(_promptDirectory, prompt.FileName);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, prompt.Content);
            }
            else
            {
                prompt.Content = File.ReadAllText(path);
            }
        }

        return prompts;
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

    private static List<PromptTemplate> GetDefaultPrompts()
    {
        List<PromptTemplate> templates = new List<PromptTemplate>
        {
            new()
            {
                Key = "manager-master",
                DisplayName = "Manager: Mode Selection",
                FileName = "manager-master.txt",
                Content = "Role: Manager agent for KanBeast.\n\nPurpose:\n- Decide the current workflow mode from ticket status, tasks, activity log, and test results.\n- Choose one mode: Breakdown, Assign, Verify, Accept, Testing.\n\nProcess:\n1) State the chosen mode and why.\n2) Follow the matching mode prompt.\n3) If evidence is insufficient, ask for clarification and stop.\n\nTone: concise, direct, and outcome-focused."
            },
            new()
            {
                Key = "manager-breakdown",
                DisplayName = "Manager: Break Down Ticket",
                FileName = "manager-breakdown.txt",
                Content = "Role: Manager agent in Breakdown mode.\n\nOutput:\n- Provide an ordered list of small, testable subtasks.\n- Each task includes acceptance criteria and any key constraints.\n\nRules:\n- Keep tasks independent and measurable.\n- Avoid vague phrasing.\n- Ask for missing details instead of guessing."
            },
            new()
            {
                Key = "manager-assign",
                DisplayName = "Manager: Assign Task",
                FileName = "manager-assign.txt",
                Content = "Role: Manager agent in Assign mode.\n\nOutput:\n- Select the next incomplete task.\n- Restate the goal and acceptance criteria.\n- List constraints and relevant files to inspect.\n\nProcess:\n- Confirm readiness before work starts.\n- Do not assign multiple tasks at once."
            },
            new()
            {
                Key = "manager-verify",
                DisplayName = "Manager: Verify Task",
                FileName = "manager-verify.txt",
                Content = "Role: Manager agent in Verify mode.\n\nProcess:\n- Verify work against acceptance criteria.\n- Use tools when needed to check files, outputs, and tests.\n\nOutput:\n- Respond with APPROVED or REJECTED: <reason>.\n- If rejected, provide precise, actionable fixes."
            },
            new()
            {
                Key = "manager-accept",
                DisplayName = "Manager: Accept Task",
                FileName = "manager-accept.txt",
                Content = "Role: Manager agent in Accept mode.\n\nProcess:\n- Mark the verified task complete and update the ticket.\n- Move to the next task or transition to Testing if all tasks are complete.\n\nOutput:\n- Provide a short completion summary and next step."
            },
            new()
            {
                Key = "manager-testing",
                DisplayName = "Manager: Testing Phase",
                FileName = "manager-testing.txt",
                Content = "Role: Manager agent in Testing mode.\n\nProcess:\n- Ensure relevant tests are present and executed.\n- If tests fail, return to Active with remediation steps.\n- Only mark Done after all tests pass and changes are ready to commit."
            },
            new()
            {
                Key = "developer-implementation",
                DisplayName = "Developer: Implement Features",
                FileName = "developer-implementation.txt",
                Content = "Role: Developer agent implementing tasks in a codebase.\n\nProcess:\n- Read relevant files before editing.\n- Follow repository conventions and minimize changes.\n- Use available tools for file edits and commands.\n\nOutput:\n- Summarize changes and test results.\n- If blocked, explain what is missing."
            },
            new()
            {
                Key = "developer-testing",
                DisplayName = "Developer: Test Changes",
                FileName = "developer-testing.txt",
                Content = "Role: Developer agent in testing mode.\n\nProcess:\n- Add or update tests as needed.\n- Run relevant test suites and analyze failures.\n- Fix issues and re-run until green.\n\nOutput:\n- Report test commands and results."
            },
            new()
            {
                Key = "manager-compaction-summary",
                DisplayName = "Manager: Compaction Summary Prompt",
                FileName = "manager-compaction-summary.txt",
                Content = "Write a continuation summary for the manager agent.\n\nInclude:\n1) Task overview and success criteria.\n2) Completed work and verified outcomes.\n3) Open issues, risks, or blockers.\n4) Next steps in priority order.\n5) Key context that must be preserved.\n\nKeep it concise, factual, and actionable. Wrap the summary in <summary></summary> tags."
            },
            new()
            {
                Key = "manager-compaction-system",
                DisplayName = "Manager: Compaction System Prompt",
                FileName = "manager-compaction-system.txt",
                Content = "You summarize manager context so work can continue after compaction. Keep output concise, structured, and factual. Do not invent details."
            },
            new()
            {
                Key = "developer-compaction-summary",
                DisplayName = "Developer: Compaction Summary Prompt",
                FileName = "developer-compaction-summary.txt",
                Content = "Write a continuation summary for the developer agent.\n\nInclude:\n1) Task overview and acceptance criteria.\n2) Code changes made (files and key edits).\n3) Tests run and results.\n4) Problems found and fixes applied.\n5) Remaining work and next steps.\n\nKeep it concise, factual, and actionable. Wrap the summary in <summary></summary> tags."
            },
            new()
            {
                Key = "developer-compaction-system",
                DisplayName = "Developer: Compaction System Prompt",
                FileName = "developer-compaction-system.txt",
                Content = "You summarize developer context so work can continue after compaction. Keep output concise, structured, and factual. Do not invent details."
            }
        };

        return templates;
    }

    private SettingsFile LoadSettingsFile()
    {
        SettingsFile settingsFile = new SettingsFile();

        if (File.Exists(_settingsPath))
        {
            string json = File.ReadAllText(_settingsPath);
            JsonSerializerOptions options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            SettingsFile? parsedSettings = JsonSerializer.Deserialize<SettingsFile>(json, options);
            if (parsedSettings != null)
            {
                settingsFile = parsedSettings;
            }
        }

        return settingsFile;
    }

    private void SaveSettingsFile(SettingsFile settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath) ?? string.Empty);
        JsonSerializerOptions options = new JsonSerializerOptions
        {
            WriteIndented = true
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
            LlmRetryCount = fileSettings.LlmRetryCount,
            LlmRetryDelaySeconds = fileSettings.LlmRetryDelaySeconds,
            ManagerCompaction = fileSettings.ManagerCompaction,
            DeveloperCompaction = fileSettings.DeveloperCompaction,
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
            LlmRetryCount = settings.LlmRetryCount,
            LlmRetryDelaySeconds = settings.LlmRetryDelaySeconds,
            ManagerCompaction = settings.ManagerCompaction,
            DeveloperCompaction = settings.DeveloperCompaction
        };

        return fileSettings;
    }
}
