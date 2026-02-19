using System.Collections.Concurrent;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Shared state for the current worker run. Set once when processing begins, cleared when done.
// These values are effectively global for the lifetime of a ticket's processing.
public static class WorkerSession
{
	public static IKanbanApiClient ApiClient { get; private set; } = null!;
	public static LlmProxy LlmProxy { get; private set; } = null!;
	public static Dictionary<string, string> Prompts { get; private set; } = null!;
	public static TicketHolder TicketHolder { get; private set; } = null!;
	public static string WorkDir { get; private set; } = string.Empty;
	public static CancellationToken CancellationToken { get; private set; }
	public static WorkerHubClient HubClient { get; private set; } = null!;
	public static WebSearchConfig WebSearch { get; private set; } = new();
	public static CompactionSettings Compaction { get; private set; } = new();

	public static ConcurrentQueue<string> GetChatQueue(string conversationId)
	{
		return HubClient.GetChatQueue(conversationId);
	}

	public static void Start(
		IKanbanApiClient apiClient,
		LlmProxy llmProxy,
		Dictionary<string, string> prompts,
		TicketHolder ticketHolder,
		string workDir,
		CancellationToken cancellationToken,
		WorkerHubClient hubClient,
		WebSearchConfig webSearch,
		CompactionSettings compaction)
	{
		ApiClient = apiClient;
		LlmProxy = llmProxy;
		Prompts = prompts;
		TicketHolder = ticketHolder;
		WorkDir = workDir;
		CancellationToken = cancellationToken;
		HubClient = hubClient;
		WebSearch = webSearch;
		Compaction = compaction;
	}

	public static void UpdateCancellationToken(CancellationToken cancellationToken)
	{
		CancellationToken = cancellationToken;
	}

	public static void Stop()
	{
		ApiClient = null!;
		LlmProxy = null!;
		Prompts = null!;
		TicketHolder = null!;
		WorkDir = string.Empty;
		CancellationToken = default;
		HubClient = null!;
		WebSearch = new();
		Compaction = new();
	}
}
