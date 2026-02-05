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
    private readonly bool _jsonLogging;
    private int _currentLlmIndex;
    private int _conversationIndex;

    public string LogDirectory => _logDirectory;

    public string LogPrefix => _logPrefix;

    public bool JsonLogging => _jsonLogging;

    public ICompaction Compaction => _compaction;

    public LlmProxy(List<LLMConfig> configs, int retryCount, int retryDelaySeconds, ICompaction compaction, string logDirectory, string logPrefix, bool jsonLogging)
    {
        _configs = configs;
        _retryCount = retryCount;
        _retryDelay = TimeSpan.FromSeconds(retryDelaySeconds);
        _compaction = compaction;
        _logDirectory = logDirectory;
        _logPrefix = logPrefix;
        _jsonLogging = jsonLogging;
        _currentLlmIndex = 0;
        _conversationIndex = 0;
    }

    public LlmConversation CreateConversation(string systemPrompt, string userPrompt)
    {
        if (_currentLlmIndex >= _configs.Count)
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        _conversationIndex++;
        string logPath = string.Empty;
        if (!string.IsNullOrWhiteSpace(_logDirectory) && !string.IsNullOrWhiteSpace(_logPrefix))
        {
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            logPath = Path.Combine(_logDirectory, $"{_logPrefix}-{timestamp}-{_conversationIndex:D3}.log");
        }

        LlmConversation conversation = new LlmConversation(_configs[_currentLlmIndex].Model, systemPrompt, userPrompt, logPath);
        return conversation;
    }

    public async Task<LlmResult> ContinueAsync(LlmConversation conversation, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
    {
        LlmResult result = new LlmResult();
        bool succeeded = false;

        if (_currentLlmIndex >= _configs.Count)
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        while (_currentLlmIndex < _configs.Count && !succeeded)
        {
            LLMConfig config = _configs[_currentLlmIndex];
            string modelName = config.Model;
            LlmService service = new LlmService(config, _compaction, _jsonLogging);

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

        if (!succeeded)
        {
            throw new InvalidOperationException("All configured LLMs are unavailable.");
        }

        return result;
    }

    public async Task FinalizeConversationAsync(LlmConversation conversation, CancellationToken cancellationToken)
    {
        conversation.MarkCompleted();
        if (_jsonLogging)
        {
            await conversation.WriteLogAsync("-complete", cancellationToken);
        }
    }

	public async Task<LlmResult> RunAsync(string systemPrompt, string userPrompt, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
	{
		LlmConversation conversation = CreateConversation(systemPrompt, userPrompt);
		LlmResult result = await ContinueAsync(conversation, providers, role, cancellationToken);
		await FinalizeConversationAsync(conversation, cancellationToken);
		return result;
	}
}
