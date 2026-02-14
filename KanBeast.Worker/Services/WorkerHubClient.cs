using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Shared;
using Microsoft.AspNetCore.SignalR.Client;

namespace KanBeast.Worker.Services;

// SignalR client that connects the worker to the server hub.
public class WorkerHubClient : IAsyncDisposable
{
	private readonly HubConnection _connection;
	private readonly string _ticketId;
	private readonly ConcurrentDictionary<string, ConcurrentQueue<string>> _pendingChatMessages = new();
	private readonly SemaphoreSlim _ticketChangedSignal = new SemaphoreSlim(0);
	private readonly object _activeWorkLock = new object();

	private CancellationTokenSource? _activeWorkCts;

	public WorkerHubClient(string serverUrl, string ticketId)
	{
		_ticketId = ticketId;

		string hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/kanban";

		_connection = new HubConnectionBuilder()
			.WithUrl(hubUrl)
			.WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
			.Build();

		// Listen for ticket updates pushed from the server.
		_connection.On<JsonElement>("TicketUpdated", ticket =>
		{
			if (ticket.TryGetProperty("id", out JsonElement idProp) && idProp.GetString() == _ticketId)
			{
				if (ticket.TryGetProperty("status", out JsonElement statusProp))
				{
					string? status = statusProp.ValueKind == JsonValueKind.Number
						? ((int)statusProp.GetInt32()).ToString()
						: statusProp.GetString();

					bool isActive = string.Equals(status, "Active", StringComparison.OrdinalIgnoreCase) || status == "1";
					if (!isActive)
					{
						lock (_activeWorkLock)
						{
							if (_activeWorkCts != null && !_activeWorkCts.IsCancellationRequested)
							{
								_activeWorkCts.Cancel();
							}
						}
					}
				}

				_ticketChangedSignal.Release();
			}
		});

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

	// Creates a linked CancellationToken that is also cancelled when the ticket leaves Active.
	public CancellationToken BeginActiveWork(CancellationToken parentToken)
	{
		lock (_activeWorkLock)
		{
			_activeWorkCts?.Dispose();
			_activeWorkCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
			return _activeWorkCts.Token;
		}
	}

	// Disposes the active-work CTS. Call after the work loop catches cancellation.
	public void EndActiveWork()
	{
		lock (_activeWorkLock)
		{
			_activeWorkCts?.Dispose();
			_activeWorkCts = null;
		}
	}

	public async Task WaitForTicketChangeAsync(CancellationToken cancellationToken)
	{
		await _ticketChangedSignal.WaitAsync(cancellationToken);
	}

	public void DrainPendingSignals()
	{
		while (_ticketChangedSignal.CurrentCount > 0)
		{
			_ticketChangedSignal.Wait(0);
		}
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

	// Pushes the full conversation snapshot to the server.
	public async Task SyncConversationAsync(ConversationData data)
	{
		await HubSendAsync("SyncConversation", [_ticketId, data]);
	}

	// Marks a conversation as finished on the server.
	public async Task FinishConversationAsync(string conversationId)
	{
		await HubSendAsync("FinishConversation", [_ticketId, conversationId]);
	}

	// Fire-and-forget send with one retry. Uses SendCoreAsync instead of InvokeAsync
	// to avoid server response timeouts when the connection is stale but still reports Connected.
	private async Task HubSendAsync(string method, object?[] args)
	{
		bool sent = false;

		for (int attempt = 0; attempt < 2 && !sent; attempt++)
		{
			if (_connection.State != HubConnectionState.Connected)
			{
				await Task.Delay(TimeSpan.FromSeconds(5));
			}

			if (_connection.State == HubConnectionState.Connected)
			{
				try
				{
					await _connection.SendCoreAsync(method, args);
					sent = true;
				}
				catch (Exception ex) when (ex is not OperationCanceledException)
				{
					Console.WriteLine($"Hub {method} attempt {attempt + 1} failed: {ex.Message}");
				}
			}
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
