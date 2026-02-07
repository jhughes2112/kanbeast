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

	// From the perspective of the caller, this method will keep retrying with fallback LLMs until it gets a successful response or exhausts all options.
	// The caller just sees a single async call that either succeeds or fails after all retries.  If this errors, there is no LLM available.
	// remainingBudget of 0 or less means unlimited.
	public async Task<LlmResult> ContinueAsync(LlmConversation conversation, List<Tool> tools, decimal remainingBudget, CancellationToken cancellationToken)
	{
		for (;;)
		{
			if (_currentLlmIndex >= _services.Count)
			{
				return new LlmResult { ExitReason = LlmExitReason.LlmCallFailed, ErrorMessage = "All configured LLMs failed" };
			}

			LlmService service = _services[_currentLlmIndex];
			string modelName = _configs[_currentLlmIndex].Model;

			LlmResult result = await service.RunAsync(conversation, tools, _compaction, remainingBudget, cancellationToken);

			if (result.ExitReason != LlmExitReason.LlmCallFailed)
			{
				return result;
			}

			Console.WriteLine($"LLM {_currentLlmIndex} ({modelName}) failed: {result.ErrorMessage}. Trying next...");
			_currentLlmIndex++;
		}
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
