using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;

namespace KanBeast.Worker.Services;

// Simplified conversation message — worker formats tool calls as strings.
public class ConversationMessage
{
	public string Role { get; set; } = string.Empty;
	public string? Content { get; set; }
	public string? ToolCall { get; set; }
	public string? ToolResult { get; set; }
}

// SignalR client that connects the worker to the server hub.
public class WorkerHubClient : IAsyncDisposable
{
	private readonly HubConnection _connection;
	private readonly string _ticketId;
	private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingChatMessages = new();

	public WorkerHubClient(string serverUrl, string ticketId)
	{
		_ticketId = ticketId;

		string hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/kanban";

		_connection = new HubConnectionBuilder()
			.WithUrl(hubUrl)
			.WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
			.Build();

		// Listen for chat messages from the browser, routed through the server hub.
		_connection.On<string, string, string>("WorkerChatMessage", (fromTicketId, conversationId, message) =>
		{
			if (fromTicketId == _ticketId && !string.IsNullOrWhiteSpace(message))
			{
				ConcurrentQueue<string> queue = _pendingChatMessages.GetOrAdd(conversationId, _ => new ConcurrentQueue<string>());
				queue.Enqueue(message);
			}
		});
	}

	public ConcurrentQueue<string> GetChatQueue(string conversationId)
	{
		return _pendingChatMessages.GetOrAdd(conversationId, _ => new ConcurrentQueue<string>());
	}

	public async Task ConnectAsync(CancellationToken cancellationToken)
	{
		await _connection.StartAsync(cancellationToken);

		// Register this worker with its ticket id so the hub can route messages to it.
		await _connection.InvokeAsync("RegisterWorker", _ticketId, cancellationToken);
	}

	// Registers a new conversation on the server.
	public async Task RegisterConversationAsync(string conversationId, string displayName)
	{
		if (_connection.State != HubConnectionState.Connected)
		{
			return;
		}

		try
		{
			await _connection.InvokeAsync("RegisterConversation", _ticketId, conversationId, displayName);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Hub register conversation failed: {ex.Message}");
		}
	}

	// Appends new messages to a conversation on the server.
	public async Task AppendMessagesAsync(string conversationId, List<ConversationMessage> messages)
	{
		if (_connection.State != HubConnectionState.Connected || messages.Count == 0)
		{
			return;
		}

		try
		{
			await _connection.InvokeAsync("AppendConversationMessages", _ticketId, conversationId, messages);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Hub append messages failed: {ex.Message}");
		}
	}

	// Replaces all messages in a conversation on the server (after compaction).
	public async Task ResetConversationAsync(string conversationId, List<ConversationMessage> messages)
	{
		if (_connection.State != HubConnectionState.Connected)
		{
			return;
		}

		try
		{
			await _connection.InvokeAsync("ResetConversation", _ticketId, conversationId, messages);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Hub reset conversation failed: {ex.Message}");
		}
	}

	// Marks a conversation as finished on the server.
	public async Task FinishConversationAsync(string conversationId)
	{
		if (_connection.State != HubConnectionState.Connected)
		{
			return;
		}

		try
		{
			await _connection.InvokeAsync("FinishConversation", _ticketId, conversationId);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"Hub finish conversation failed: {ex.Message}");
		}
	}

	public async ValueTask DisposeAsync()
	{
		try
		{
			await _connection.StopAsync();
		}
		catch
		{
			// Best effort.
		}

		await _connection.DisposeAsync();
	}
}
