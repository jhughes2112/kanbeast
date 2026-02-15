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

	public bool IsPaid => InputTokenPrice > 0 || OutputTokenPrice > 0;
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

// Configures web search provider for agents.
public class WebSearchConfig
{
	public string Provider { get; set; } = "duckduckgo";
	public string? GoogleApiKey { get; set; }
	public string? GoogleSearchEngineId { get; set; }
}

// Defines settings persisted in settings.json. Shared by server and worker.
public class SettingsFile
{
	public List<LLMConfig> LLMConfigs { get; set; } = new();
	public GitConfig GitConfig { get; set; } = new();
	public CompactionSettings Compaction { get; set; } = new();
	public WebSearchConfig WebSearch { get; set; } = new();
}
