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
    private readonly string _logDirectory;
    private readonly string _logPrefix;
    private int _currentLlmIndex;

    public string LogDirectory => _logDirectory;

    public string LogPrefix => _logPrefix;

    public LlmProxy(List<LLMConfig> configs, int retryCount, int retryDelaySeconds, ICompaction compaction, string logDirectory, string logPrefix)
    {
        _configs = configs;
        _retryCount = retryCount;
        _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        _compaction = compaction;
        _logDirectory = logDirectory;
        _logPrefix = logPrefix;
        _currentLlmIndex = 0;

        Console.WriteLine($"LlmProxy initialized with {configs.Count} LLM config(s)");
        foreach (LLMConfig config in configs)
        {
            Console.WriteLine($"  - Model: {config.Model}, Endpoint: {config.Endpoint ?? "default"}, ContextLength: {config.ContextLength}");
        }
    }

    public async Task<LlmResult> RunAsync(string systemPrompt, string userPrompt, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
    {
        string resolvedSystemPrompt = systemPrompt;
        LlmResult result = new LlmResult();
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
                    LlmResult iterationResult = await service.RunAsync(conversation, providers, role, cancellationToken);
                    result.Content = iterationResult.Content;
                    result.AccumulatedCost += iterationResult.AccumulatedCost;
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

        await conversation.WriteLogAsync(_logDirectory, _logPrefix, "complete", cancellationToken);
        if (succeeded)
        {
            conversation.MarkCompleted();
        }
        else
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        return result;
    }
}
