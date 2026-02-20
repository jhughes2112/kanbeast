namespace KanBeast.Shared;

// Describes a single LLM endpoint.
public class LLMConfig
{
	public string Id { get; set; } = Guid.NewGuid().ToString();
	public string ApiKey { get; set; } = string.Empty;
	public string Model { get; set; } = string.Empty;
	public string? Endpoint { get; set; }
	public int ContextLength { get; set; } = 128000;
	public decimal InputTokenPrice { get; set; } = 0m;
	public decimal OutputTokenPrice { get; set; } = 0m;
	public double Temperature { get; set; } = 0.2;
	public string Strengths { get; set; } = string.Empty;
	public string Weaknesses { get; set; } = string.Empty;

	// Combined cost per 1M tokens (input + output) for relative cost ranking.
	public decimal CostPer1MTokens => InputTokenPrice + OutputTokenPrice;
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
	public string? ApiKey { get; set; }
	public string Model { get; set; } = "openai/gpt-4.1-nano";
	public string Engine { get; set; } = "auto";
}

// Defines settings persisted in settings.json. Shared by server and worker.
public class SettingsFile
{
	public List<LLMConfig> LLMConfigs { get; set; } = new();
	public GitConfig GitConfig { get; set; } = new();
	public CompactionSettings Compaction { get; set; } = new();
	public WebSearchConfig WebSearch { get; set; } = new();
}
