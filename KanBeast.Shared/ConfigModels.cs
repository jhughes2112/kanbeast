namespace KanBeast.Shared;

// Describes a single LLM model configuration.
public class LLMConfig
{
	public string Id { get; }
	public string Model { get; }
	public int ContextLength { get; }
	public decimal InputTokenPrice { get; }
	public decimal OutputTokenPrice { get; }
	public double Temperature { get; }
	public string Strengths { get; }
	public string Weaknesses { get; }

	// Combined cost per 1M tokens (input + output) for relative cost ranking.
	public decimal CostPer1MTokens => InputTokenPrice + OutputTokenPrice;

	[JsonConstructor]
	public LLMConfig(
		string? id,
		string model,
		int contextLength,
		decimal inputTokenPrice,
		decimal outputTokenPrice,
		double temperature,
		string strengths,
		string weaknesses)
	{
		Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString() : id;
		Model = model ?? string.Empty;
		ContextLength = contextLength;
		InputTokenPrice = inputTokenPrice;
		OutputTokenPrice = outputTokenPrice;
		Temperature = temperature;
		Strengths = strengths ?? string.Empty;
		Weaknesses = weaknesses ?? string.Empty;
	}

	public LLMConfig(string model, int contextLength, decimal inputTokenPrice, decimal outputTokenPrice, double temperature, string strengths, string weaknesses)
		: this(null, model, contextLength, inputTokenPrice, outputTokenPrice, temperature, strengths, weaknesses)
	{
	}
}

// Git integration settings.
public class GitConfig
{
	public string RepositoryUrl { get; set; } = string.Empty;
	public string? SshKey { get; set; }
	public string? Password { get; set; }
	public string? ApiToken { get; set; }
	public string Username { get; set; } = string.Empty;
	public string Email { get; set; } = string.Empty;
}

// Configures compaction behavior for agent context handling.
public class CompactionSettings
{
	public string Type { get; set; } = "summarize";
	public double ContextSizePercent { get; set; } = 0.9;
}

// Configures web search via OpenRouter's web plugin.
// Engine selects how results are fetched: "auto" (default), "native", or "exa".
public class WebSearchConfig
{
	public string Model { get; set; } = "openai/gpt-4.1-nano";
	public string Engine { get; set; } = "auto";
}

// Defines settings persisted in settings.json. Shared by server and worker.
public class SettingsFile
{
	// Shared provider endpoint and API key used by all LLMs and web search.
	public string Endpoint { get; set; } = "https://openrouter.ai/api/v1";
	public string ApiKey { get; set; } = string.Empty;

	public List<LLMConfig> LLMConfigs { get; set; } = new();
	public GitConfig GitConfig { get; set; } = new();
	public CompactionSettings Compaction { get; set; } = new();
	public WebSearchConfig WebSearch { get; set; } = new();
}
