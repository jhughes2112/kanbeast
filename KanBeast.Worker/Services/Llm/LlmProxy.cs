using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with retry, fallback, and context handling.
public class LlmProxy
{
    private readonly List<LLMConfig> _configs;
    private readonly int _retryCount;
    private readonly TimeSpan _retryDelay;
    private readonly ICompaction _compaction;
    private int _currentLlmIndex;
    private string _logDirectory = string.Empty;
    private string _logPrefix = string.Empty;

    public string LogDirectory
    {
        get => _logDirectory;
        set => _logDirectory = value;
    }

    public string LogPrefix
    {
        get => _logPrefix;
        set => _logPrefix = value;
    }

    public LlmProxy(List<LLMConfig> configs, int retryCount, int retryDelaySeconds, ICompaction compaction)
    {
        _configs = configs;
        _retryCount = retryCount;
        _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        _compaction = compaction;
        _currentLlmIndex = 0;

        Console.WriteLine($"LlmProxy initialized with {configs.Count} LLM config(s)");
        foreach (LLMConfig config in configs)
        {
            Console.WriteLine($"  - Model: {config.Model}, Endpoint: {config.Endpoint ?? "default"}, ContextLength: {config.ContextLength}");
        }
    }

    public async Task<string> RunAsync(string systemPrompt, string userPrompt, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
    {
        string resolvedSystemPrompt = systemPrompt;
        string result = string.Empty;
        bool succeeded = false;

        if (_currentLlmIndex >= _configs.Count)
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        LlmConversation conversation = new LlmConversation(_configs[_currentLlmIndex].Model, resolvedSystemPrompt, userPrompt);

        while (_currentLlmIndex < _configs.Count && !succeeded)
        {
            LLMConfig config = _configs[_currentLlmIndex];
            string modelName = config.Model;
            LlmService service = new LlmService(config, _compaction, _logDirectory, _logPrefix);

            int attempt = 0;
            while (attempt <= _retryCount && !succeeded)
            {
                try
                {
                    result = await service.RunAsync(conversation, providers, role, cancellationToken);
                    succeeded = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"LLM {_currentLlmIndex} ({modelName}) attempt {attempt + 1} failed: {ex.Message}");
                    if (attempt < _retryCount)
                    {
                        if (_retryDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(_retryDelay, cancellationToken);
                        }
                    }
                    else
                    {
                        Console.WriteLine($"LLM {_currentLlmIndex} ({modelName}) marked as down after {_retryCount + 1} attempts");
                    }
                }

                attempt++;
            }

            if (!succeeded)
            {
                _currentLlmIndex++;
            }
        }

        if (succeeded)
        {
            conversation.MarkCompleted();
            await conversation.WriteLogAsync(_logDirectory, _logPrefix, "complete", cancellationToken);
        }
        else
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        return result;
    }
}
