using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with retry and fallback.
public class LlmProxy
{
	private readonly List<LlmService> _services;
	private readonly ICompaction _compaction;
	private int _currentLlmIndex;

	public LlmProxy(List<LLMConfig> configs, ICompaction compaction, bool jsonLogging)
	{
		_compaction = compaction;
		_currentLlmIndex = 0;

		_services = new List<LlmService>();
		foreach (LLMConfig config in configs)
		{
			_services.Add(new LlmService(config, jsonLogging));
		}
	}

	public string CurrentModel => _currentLlmIndex < _services.Count ? _services[_currentLlmIndex].Model : "none";

	// Runs the conversation through the current LLM, with fallback to next LLM on failure.
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

			LlmResult result = await service.RunAsync(conversation, tools, _compaction, remainingBudget, cancellationToken);

			if (result.ExitReason != LlmExitReason.LlmCallFailed)
			{
				return result;
			}

			Console.WriteLine($"LLM {_currentLlmIndex} ({service.Model}) failed: {result.ErrorMessage}. Trying next...");
			_currentLlmIndex++;
		}
	}
}
