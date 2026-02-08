using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with availability-aware fallback.
//
// Each LlmService tracks its own health: whether it supports tool_choice, whether it is
// rate-limited or down, and when it will next be available. The proxy picks the best
// available service (preferring the configured primary), and on rate-limit or failure
// immediately tries the next available one. If all are busy it waits for the soonest.
//
public class LlmProxy
{
	private readonly List<LlmService> _services;
	private readonly ICompaction _compaction;
	private int _preferredIndex;

	public LlmProxy(List<LLMConfig> configs, ICompaction compaction, bool jsonLogging)
	{
		_compaction = compaction;
		_preferredIndex = 0;

		_services = new List<LlmService>();
		foreach (LLMConfig config in configs)
		{
			_services.Add(new LlmService(config, jsonLogging));
		}
	}

	public string CurrentModel => _preferredIndex < _services.Count ? _services[_preferredIndex].Model : "none";

	// Resets preferred LLM to the first configured endpoint.
	// Call at natural boundaries (new subtask, new conversation) to prefer the primary LLM again.
	public void ResetFallback()
	{
		_preferredIndex = 0;
	}

	// Runs the conversation, selecting available LLMs and retrying on rate limits or failures.
	// remainingBudget of 0 or less means unlimited.
	public async Task<LlmResult> ContinueAsync(LlmConversation conversation, List<Tool> tools, decimal remainingBudget, int? maxCompletionTokens, CancellationToken cancellationToken)
	{
		for (;;)
		{
			cancellationToken.ThrowIfCancellationRequested();

			LlmService? service = FindAvailableService();

			if (service == null)
			{
				DateTimeOffset soonest = FindSoonestAvailableTime();
				TimeSpan waitTime = soonest - DateTimeOffset.UtcNow;

				if (waitTime > TimeSpan.FromMinutes(10))
				{
					return new LlmResult { ExitReason = LlmExitReason.LlmCallFailed, ErrorMessage = "All configured LLMs are down" };
				}

				if (waitTime > TimeSpan.Zero)
				{
					Console.WriteLine($"All LLMs busy, waiting {waitTime.TotalSeconds:F0}s for next available...");
					await Task.Delay(waitTime, cancellationToken);
				}

				continue;
			}

			LlmResult result = await service.RunAsync(conversation, tools, _compaction, remainingBudget, maxCompletionTokens, cancellationToken);

			if (result.ExitReason == LlmExitReason.RateLimited)
			{
				Console.WriteLine($"LLM ({service.Model}) rate limited until {result.RetryAfter:HH:mm:ss}. Trying next available...");
				continue;
			}

			if (result.ExitReason == LlmExitReason.LlmCallFailed)
			{
				Console.WriteLine($"LLM ({service.Model}) failed: {result.ErrorMessage}. Trying next available...");
				continue;
			}

			// Success, max iterations, cost exceeded, or tool exit.
			_preferredIndex = _services.IndexOf(service);
			return result;
		}
	}

	// Forces a compaction pass on the conversation to hoist memories before the conversation is discarded.
	public async Task<decimal> CompactAsync(LlmConversation conversation, decimal remainingBudget, CancellationToken cancellationToken)
	{
		LlmService? service = FindAvailableService();
		if (service == null)
		{
			throw new InvalidOperationException("No LLM service available for compaction");
		}

		return await _compaction.CompactAsync(conversation, service, remainingBudget, cancellationToken);
	}

	private LlmService? FindAvailableService()
	{
		// Prefer the current preferred service.
		if (_preferredIndex < _services.Count && _services[_preferredIndex].IsAvailable)
		{
			return _services[_preferredIndex];
		}

		// Try others in configured order.
		for (int i = 0; i < _services.Count; i++)
		{
			if (_services[i].IsAvailable)
			{
				return _services[i];
			}
		}

		return null;
	}

	private DateTimeOffset FindSoonestAvailableTime()
	{
		DateTimeOffset soonest = DateTimeOffset.MaxValue;

		foreach (LlmService service in _services)
		{
			if (service.AvailableAt < soonest)
			{
				soonest = service.AvailableAt;
			}
		}

		return soonest;
	}
}
