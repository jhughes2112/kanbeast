using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Coordinates multiple LLM endpoints with availability tracking.
// Each LlmService tracks its own health: whether it supports tool_choice, whether it is
// rate-limited or down, and when it will next be available. The proxy does NOT silently
// fall back between models — the planning agent chooses which LLM to use and is told
// when its choice is unavailable so it can pick a different one.
public class LlmProxy
{
	private const int ShortWaitThresholdSeconds = 20;

	private readonly List<LlmService> _services;

	public LlmProxy(List<LLMConfig> configs)
	{
		_services = new List<LlmService>();
		foreach (LLMConfig config in configs)
		{
			_services.Add(new LlmService(config));
		}
	}

	// Replaces the service list with fresh instances, resetting all disabled/rate-limited state.
	public void UpdateConfigs(List<LLMConfig> configs)
	{
		_services.Clear();
		foreach (LLMConfig config in configs)
		{
			_services.Add(new LlmService(config));
		}

		Console.WriteLine($"LlmProxy: Updated to {configs.Count} LLM config(s)");
	}

	// Runs one LLM turn on the conversation using the specified LLM config.
	// Waits for short rate limits (≤20s), returns error for longer ones or failures.
	public async Task<LlmResult> ContinueAsync(string configId, ILlmConversation conversation, int? maxCompletionTokens, CancellationToken cancellationToken)
	{
		cancellationToken.ThrowIfCancellationRequested();

		conversation.ToolContext.LlmConfigId = configId;

		decimal remainingBudget = conversation.GetRemainingBudget();
		if (remainingBudget > 0 && remainingBudget <= 0)
		{
			return new LlmResult { ExitReason = LlmExitReason.CostExceeded };
		}

		LlmService? service = null;
		foreach (LlmService s in _services)
		{
			if (s.Config.Id == configId)
			{
				service = s;
				break;
			}
		}

		if (service == null)
		{
			return new LlmResult { ExitReason = LlmExitReason.LlmCallFailed, ErrorMessage = $"No LLM config found with id '{configId}'" };
		}

		if (service.IsPermanentlyDown)
		{
			return new LlmResult { ExitReason = LlmExitReason.LlmCallFailed, ErrorMessage = $"LLM {service.Model} is permanently down" };
		}

		if (!service.IsAvailable)
		{
			TimeSpan waitTime = service.AvailableAt - DateTimeOffset.UtcNow;
			if (waitTime.TotalSeconds > ShortWaitThresholdSeconds)
			{
				return new LlmResult { ExitReason = LlmExitReason.RateLimited, RetryAfter = service.AvailableAt, ErrorMessage = $"LLM {service.Model} rate limited for {waitTime.TotalSeconds:F0}s" };
			}
			if (waitTime > TimeSpan.Zero)
			{
				Console.WriteLine($"LLM {service.Model} rate limited, waiting {waitTime.TotalSeconds:F0}s...");
				await Task.Delay(waitTime, cancellationToken);
			}
		}

		(LlmResult result, decimal cost) = await service.RunAsync(conversation, remainingBudget, maxCompletionTokens, cancellationToken);
		await conversation.RecordCostAsync(cost, cancellationToken);

		return result;
	}

	// Builds a summary of available LLMs for the planning agent to choose from.
	// Excludes models too expensive for the remaining budget (0 = unlimited).
	public List<(string id, string model, string strengths, string weaknesses, decimal costPer1MTokens, bool isAvailable)> GetAvailableLlmSummaries(decimal remainingBudget)
	{
		List<(string id, string model, string strengths, string weaknesses, decimal costPer1MTokens, bool isAvailable)> summaries = new();

		foreach (LlmService service in _services)
		{
			LLMConfig config = service.Config;

			// Skip models we can't afford at least 1M tokens from.
			if (remainingBudget > 0 && config.CostPer1MTokens > 0 && config.CostPer1MTokens > remainingBudget)
			{
				continue;
			}

			summaries.Add((config.Id, config.Model, config.Strengths, config.Weaknesses, config.CostPer1MTokens, !service.IsPermanentlyDown));
		}

		return summaries;
	}

	// Updates the strengths and weaknesses notes on a specific LLM config in memory.
	public bool UpdateLlmNotes(string configId, string strengths, string weaknesses)
	{
		foreach (LlmService service in _services)
		{
			if (service.Config.Id == configId)
			{
				service.Config.Strengths = strengths;
				service.Config.Weaknesses = weaknesses;
				return true;
			}
		}

		return false;
	}

	// Runs the conversation in a loop
	// On MaxIterationsReached (or Completed when exitOnCompletion is false), resets iteration and nudges with continueMessage.
	// maxResets: maximum reset-and-continue cycles before giving up (0 = unlimited).
	public async Task<LlmResult> RunToCompletionAsync(
		ILlmConversation conversation,
		string configId,
		int? maxCompletionTokens,
		string continueMessage,
		int maxResets,
		bool exitOnCompletion,
		CancellationToken cancellationToken)
	{
		LlmResult finalResult = new LlmResult { ExitReason = LlmExitReason.LlmCallFailed, ErrorMessage = "RunToCompletionAsync ended unexpectedly" };

		for (int resets = 0; ; )
		{
			cancellationToken.ThrowIfCancellationRequested();

			LlmResult result = await ContinueAsync(configId, conversation, maxCompletionTokens, cancellationToken);

			bool shouldReset = false;

			if (result.ExitReason == LlmExitReason.ToolRequestedExit)
			{
				finalResult = result;
			}
			else if (result.ExitReason == LlmExitReason.Completed && exitOnCompletion)
			{
				finalResult = result;
			}
			else if (result.ExitReason == LlmExitReason.Completed || result.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				resets++;
				if (maxResets > 0 && resets >= maxResets)
				{
					result.ExitReason = LlmExitReason.MaxIterationsReached;
					finalResult = result;
				}
				else
				{
					shouldReset = true;
				}
			}
			else
			{
				finalResult = result;
			}

			if (shouldReset)
			{
				conversation.ResetIteration();
				await conversation.AddUserMessageAsync(continueMessage, cancellationToken);
			}
			else
			{
				break;
			}
		}

		return finalResult;
	}

	// Returns the smallest context length across all non-permanently-down services.
	// Compaction uses this to ensure the conversation fits in any LLM that might be used.
	public int GetSmallestContextLength()
	{
		int smallest = 0;

		foreach (LlmService service in _services)
		{
			if (service.IsPermanentlyDown)
			{
				continue;
			}

			if (service.ContextLength > 0 && (smallest == 0 || service.ContextLength < smallest))
			{
				smallest = service.ContextLength;
			}
		}

		return smallest;
	}
}
