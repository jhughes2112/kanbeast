using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

public interface ISettingsService
{
    Task<Settings> GetSettingsAsync();
    Task<Settings> UpdateSettingsAsync(Settings settings);
    Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config);
    Task<bool> RemoveLLMConfigAsync(string id);
}

public class SettingsService : ISettingsService
{
    private readonly string _promptDirectory;
    private Settings _settings;

    public SettingsService(IWebHostEnvironment environment)
    {
        _promptDirectory = Path.Combine(environment.ContentRootPath, "env", "prompts");
        Directory.CreateDirectory(_promptDirectory);
        _settings = new Settings
        {
            SystemPrompts = LoadPromptTemplates()
        };
    }

    public Task<Settings> GetSettingsAsync()
    {
        _settings.SystemPrompts = LoadPromptTemplates();
        return Task.FromResult(_settings);
    }

    public Task<Settings> UpdateSettingsAsync(Settings settings)
    {
        var prompts = settings.SystemPrompts?.Count > 0 ? settings.SystemPrompts : _settings.SystemPrompts;
        settings.SystemPrompts = UpdatePromptFiles(prompts);
        _settings = settings;
        return Task.FromResult(_settings);
    }

    public Task<LLMConfig?> AddLLMConfigAsync(LLMConfig config)
    {
        _settings.LLMConfigs.Add(config);
        return Task.FromResult<LLMConfig?>(config);
    }

    public Task<bool> RemoveLLMConfigAsync(string id)
    {
        var config = _settings.LLMConfigs.FirstOrDefault(c => c.Id == id);
        if (config == null)
            return Task.FromResult(false);

        _settings.LLMConfigs.Remove(config);
        return Task.FromResult(true);
    }

    private List<PromptTemplate> LoadPromptTemplates()
    {
        var prompts = GetDefaultPrompts();
        foreach (var prompt in prompts)
        {
            var path = Path.Combine(_promptDirectory, prompt.FileName);
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
        var updatedPrompts = new List<PromptTemplate>();
        foreach (var prompt in prompts)
        {
            var fileName = string.IsNullOrWhiteSpace(prompt.FileName)
                ? $"{prompt.Key}.txt"
                : prompt.FileName;
            var path = Path.Combine(_promptDirectory, fileName);
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
        return new List<PromptTemplate>
        {
            new()
            {
                Key = "manager-master",
                DisplayName = "Manager: Mode Selection",
                FileName = "manager-master.txt",
                Content = "You are the manager agent. First, determine the current workflow mode based on evidence (ticket status, task list, activity log, and test results). Modes: Breakdown, Assign, Verify, Accept, Testing. State the chosen mode and why, then follow the corresponding mode prompt. If evidence is insufficient, ask for clarification instead of guessing."
            },
            new()
            {
                Key = "manager-breakdown",
                DisplayName = "Manager: Break Down Ticket",
                FileName = "manager-breakdown.txt",
                Content = "You are the manager agent in Breakdown mode. Produce a numbered list of small, ordered subtasks. Each task must be specific, testable, and independently completable. Include acceptance criteria for each task."
            },
            new()
            {
                Key = "manager-assign",
                DisplayName = "Manager: Assign Task",
                FileName = "manager-assign.txt",
                Content = "You are the manager agent in Assign mode. Select the next incomplete task, restate the goal, enumerate acceptance criteria, and list any constraints or files to inspect. Confirm readiness to proceed before work begins."
            },
            new()
            {
                Key = "manager-verify",
                DisplayName = "Manager: Verify Task",
                FileName = "manager-verify.txt",
                Content = "You are the manager agent in Verify mode. Rigorously verify the work against acceptance criteria. If anything is missing, incorrect, or untested, reject the task and provide precise, actionable feedback. Only accept when everything is complete."
            },
            new()
            {
                Key = "manager-accept",
                DisplayName = "Manager: Accept Task",
                FileName = "manager-accept.txt",
                Content = "You are the manager agent in Accept mode. Mark the task complete, update the ticket, and move to the next task. If all tasks are done, transition the ticket to Testing and outline the testing/commit steps."
            },
            new()
            {
                Key = "manager-testing",
                DisplayName = "Manager: Testing Phase",
                FileName = "manager-testing.txt",
                Content = "You are the manager agent in Testing mode. Ensure tests are written and run. If any test fails, return to Active with remediation steps. Only mark the ticket Done after all tests pass and changes are ready to commit."
            },
            new()
            {
                Key = "developer-implementation",
                DisplayName = "Developer: Implement Features",
                FileName = "developer-implementation.txt",
                Content = "You are the developer agent. Implement the assigned task by editing files, executing commands, and writing clean, maintainable code. Follow existing conventions, avoid unnecessary changes, and ensure the solution builds."
            },
            new()
            {
                Key = "developer-testing",
                DisplayName = "Developer: Test Changes",
                FileName = "developer-testing.txt",
                Content = "You are the developer agent acting as the test engineer. Write or update tests, run the relevant suite, analyze failures, and fix issues. Do not report completion until tests pass."
            }
        };
    }
}
