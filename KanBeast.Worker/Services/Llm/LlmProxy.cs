using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with retry and fallback.
//
// HUNT MODE:
// On startup and after ResetFallback(), the proxy is in hunt mode. In hunt mode, each LLM
// gets minimal retries (fail fast) so we quickly find the first working endpoint. Once any
// LLM succeeds, hunt mode ends and that LLM gets full retry tolerance for the rest of the
// conversation. ResetFallback() at subtask boundaries re-enters hunt mode from index 0.
//
public class LlmProxy
{
	private readonly List<LlmService> _services;
	private readonly ICompaction _compaction;
	private int _currentLlmIndex;
	private bool _huntMode;

	public LlmProxy(List<LLMConfig> configs, ICompaction compaction, bool jsonLogging)
	{
		_compaction = compaction;
		_currentLlmIndex = 0;
		_huntMode = true;

		_services = new List<LlmService>();
		foreach (LLMConfig config in configs)
		{
			_services.Add(new LlmService(config, jsonLogging));
		}
	}

	public string CurrentModel => _currentLlmIndex < _services.Count ? _services[_currentLlmIndex].Model : "none";

	// Resets fallback state so the next call tries the primary LLM first in hunt mode.
	// Call this at natural boundaries (new subtask, new conversation) to recover from transient failures.
	public void ResetFallback()
	{
		_currentLlmIndex = 0;
		_huntMode = true;
	}

	// Runs the conversation through the current LLM, with fallback to next LLM on failure.
	// In hunt mode, failures immediately advance to the next LLM. Once one succeeds, hunt mode ends.
	// remainingBudget of 0 or less means unlimited.
	public async Task<LlmResult> ContinueAsync(LlmConversation conversation, List<Tool> tools, decimal remainingBudget, int? maxCompletionTokens, CancellationToken cancellationToken)
	{
		for (;;)
		{
			if (_currentLlmIndex >= _services.Count)
			{
				return new LlmResult { ExitReason = LlmExitReason.LlmCallFailed, ErrorMessage = "All configured LLMs failed" };
			}

			LlmService service = _services[_currentLlmIndex];

			LlmResult result = await service.RunAsync(conversation, tools, _compaction, remainingBudget, _huntMode, maxCompletionTokens, cancellationToken);

			if (result.ExitReason != LlmExitReason.LlmCallFailed)
			{
				_huntMode = false;
				return result;
			}

			Console.WriteLine($"LLM {_currentLlmIndex} ({service.Model}) failed: {result.ErrorMessage}. Trying next...");
			_currentLlmIndex++;
		}
	}
	// Forces a compaction pass on the conversation to hoist memories before the conversation is discarded.
	// Returns the cost of the compaction call.
	public async Task<decimal> CompactAsync(LlmConversation conversation, decimal remainingBudget, CancellationToken cancellationToken)
	{
		if (_currentLlmIndex >= _services.Count)
		{
			return 0m;
		}

		LlmService service = _services[_currentLlmIndex];
		return await _compaction.CompactAsync(conversation, service, remainingBudget, cancellationToken);
	}
}
