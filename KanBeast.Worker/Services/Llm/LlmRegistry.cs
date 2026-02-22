using KanBeast.Shared;

namespace KanBeast.Worker.Services;

// Registry of LLM service instances. Each LlmService tracks its own health.
// The planning agent chooses which LLM to use; this class provides lookups and summaries.
public class LlmRegistry
{
	private List<LlmService> _services;
	private string _endpoint;
	private string _apiKey;

	public LlmRegistry(string endpoint, string apiKey, List<LLMConfig> configs)
	{
		_endpoint = endpoint;
		_apiKey = apiKey;
		_services = BuildServices(endpoint, apiKey, configs);
	}

	// Replaces the service list with fresh instances, resetting all disabled/rate-limited state.
	// Builds the new list fully before swapping so GetService never sees an empty list.
	public void UpdateConfigs(string endpoint, string apiKey, List<LLMConfig> configs)
	{
		_endpoint = endpoint;
		_apiKey = apiKey;
		_services = BuildServices(endpoint, apiKey, configs);
		Console.WriteLine($"LlmProxy: Updated to {configs.Count} LLM config(s)");
	}

	private static List<LlmService> BuildServices(string endpoint, string apiKey, List<LLMConfig> configs)
	{
		List<LlmService> services = new List<LlmService>();
		foreach (LLMConfig config in configs)
		{
			services.Add(new LlmService(config, endpoint, apiKey));
		}
		return services;
	}

	// Looks up a service by config ID. Returns null if not found.
	// Captures the list reference locally to avoid races with UpdateConfigs.
	public LlmService? GetService(string configId)
	{
		List<LlmService> services = _services;
		foreach (LlmService service in services)
		{
			if (service.Config.Id == configId)
			{
				return service;
			}
		}

		return null;
	}

	// Returns the first non-permanently-down service, or null if none exist.
	public LlmService? GetFirstAvailableService()
	{
		List<LlmService> services = _services;
		foreach (LlmService service in services)
		{
			if (!service.IsPermanentlyDown)
			{
				return service;
			}
		}

		return null;
	}

	// Builds a summary of available LLMs for the planning agent to choose from.
	// Excludes models too expensive for the remaining budget (0 = unlimited).
	public List<(string id, string model, string strengths, string weaknesses, decimal costPer1MTokens, bool isAvailable)> GetAvailableLlmSummaries(decimal remainingBudget)
	{
		List<(string id, string model, string strengths, string weaknesses, decimal costPer1MTokens, bool isAvailable)> summaries = new();
		List<LlmService> services = _services;

		foreach (LlmService service in services)
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
		List<LlmService> services = _services;
		foreach (LlmService service in services)
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
}
