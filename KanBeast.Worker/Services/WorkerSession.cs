using System.Collections.Concurrent;
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
	public static WorkerHubClient? HubClient { get; private set; }

	public static ConcurrentQueue<string> GetChatQueue(string conversationId)
	{
		if (HubClient != null)
		{
			return HubClient.GetChatQueue(conversationId);
		}

		return new ConcurrentQueue<string>();
	}

	public static void Start(
		IKanbanApiClient apiClient,
		LlmProxy llmProxy,
		Dictionary<string, string> prompts,
		TicketHolder ticketHolder,
		string workDir,
		CancellationToken cancellationToken,
		WorkerHubClient? hubClient)
	{
		ApiClient = apiClient;
		LlmProxy = llmProxy;
		Prompts = prompts;
		TicketHolder = ticketHolder;
		WorkDir = workDir;
		CancellationToken = cancellationToken;
		HubClient = hubClient;
	}

	public static void Stop()
	{
		ApiClient = null!;
		LlmProxy = null!;
		Prompts = null!;
		TicketHolder = null!;
		WorkDir = string.Empty;
		CancellationToken = default;
	}
}
