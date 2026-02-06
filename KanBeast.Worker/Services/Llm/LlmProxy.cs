using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with retry, fallback, and context handling.
public class LlmProxy
{
    private readonly List<LLMConfig> _configs;
    private readonly ICompaction _compaction;
    private readonly string _logDirectory;
    private readonly string _logPrefix;
    private readonly bool _jsonLogging;
    private readonly List<LlmService> _services;
    private int _currentLlmIndex;
    private int _conversationIndex;

    public string LogDirectory => _logDirectory;

    public string LogPrefix => _logPrefix;

    public bool JsonLogging => _jsonLogging;

    public ICompaction Compaction => _compaction;

    public LlmProxy(List<LLMConfig> configs, ICompaction compaction, string logDirectory, string logPrefix, bool jsonLogging)
    {
        _configs = configs;
        _compaction = compaction;
        _logDirectory = logDirectory;
        _logPrefix = logPrefix;
        _jsonLogging = jsonLogging;
        _currentLlmIndex = 0;
        _conversationIndex = 0;

        _services = new List<LlmService>();
        foreach (LLMConfig config in configs)
        {
            _services.Add(new LlmService(config, jsonLogging));
        }
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

	public async Task<LlmResult> RunAsync(string systemPrompt, string userPrompt, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
	{
		LlmConversation conversation = CreateConversation(systemPrompt, userPrompt);
		LlmResult result = await ContinueAsync(conversation, providers, role, cancellationToken);
		await FinalizeConversationAsync(conversation, cancellationToken);
		return result;
	}

    public async Task<LlmResult> ContinueAsync(LlmConversation conversation, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
    {
        LlmResult result = new LlmResult { Success = false, ErrorMessage = "All configured LLMs failed" };

        while (_currentLlmIndex < _services.Count && !result.Success)
        {
            LlmService service = _services[_currentLlmIndex];
            string modelName = _configs[_currentLlmIndex].Model;

            result = await service.RunAsync(conversation, providers, role, _compaction, cancellationToken);

            if (!result.Success)
            {
                Console.WriteLine($"LLM {_currentLlmIndex} ({modelName}) failed: {result.ErrorMessage}. Trying next...");
                _currentLlmIndex++;
            }
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
}
