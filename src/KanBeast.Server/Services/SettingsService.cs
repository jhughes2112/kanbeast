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
    private Settings _settings = new();

    public Task<Settings> GetSettingsAsync()
    {
        return Task.FromResult(_settings);
    }

    public Task<Settings> UpdateSettingsAsync(Settings settings)
    {
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
}
