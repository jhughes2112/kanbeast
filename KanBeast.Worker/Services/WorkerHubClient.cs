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
	private readonly ConcurrentDictionary<string, bool> _pendingClearRequests = new();
	private readonly ConcurrentQueue<List<LLMConfig>> _pendingSettingsUpdates = new();
	private readonly ConcurrentQueue<Ticket> _pendingTicketUpdates = new();
	private readonly ConcurrentDictionary<string, CancellationTokenSource> _conversationInterruptSources = new();
	private readonly SemaphoreSlim _ticketChangedSignal = new SemaphoreSlim(0);
	private readonly object _activeWorkLock = new object();

	private static readonly JsonSerializerOptions _ticketJsonOptions = new JsonSerializerOptions
	{
		PropertyNameCaseInsensitive = true,
		Converters = { new JsonStringEnumConverter() }
	};

	private CancellationTokenSource? _activeWorkCts;

	public WorkerHubClient(string serverUrl, string ticketId)
	{
		_ticketId = ticketId;

		string hubUrl = $"{serverUrl.TrimEnd('/')}/hubs/kanban";

		_connection = new HubConnectionBuilder()
			.WithUrl(hubUrl)
			.WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) })
			.Build();

		// Re-register with the hub after auto-reconnect so group memberships are restored.
		_connection.Reconnected += async (connectionId) =>
		{
			Console.WriteLine($"Hub auto-reconnected (connectionId: {connectionId}), re-registering worker");
			try
			{
				await _connection.InvokeAsync("RegisterWorker", _ticketId);
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Failed to re-register worker after reconnect: {ex.Message}");
			}
		};

		// Listen for ticket updates pushed from the server.
		_connection.On<JsonElement>("TicketUpdated", ticket =>
		{
			if (ticket.TryGetProperty("id", out JsonElement idProp) && idProp.GetString() == _ticketId)
			{
				// Deserialize the full ticket so the worker can pick up field changes (e.g. PlannerLlmId).
				Ticket? parsed = JsonSerializer.Deserialize<Ticket>(ticket.GetRawText(), _ticketJsonOptions);
				if (parsed != null)
				{
					_pendingTicketUpdates.Enqueue(parsed);
				}

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
			Console.WriteLine($"Worker: WorkerChatMessage received for ticket {fromTicketId}, conversation {conversationId}");
			if (fromTicketId == _ticketId && !string.IsNullOrWhiteSpace(message))
			{
				ConcurrentQueue<string> queue = _pendingChatMessages.GetOrAdd(conversationId, _ => new ConcurrentQueue<string>());
				queue.Enqueue(message);
				Console.WriteLine($"Worker: Message enqueued for conversation {conversationId}");
			}
		});

		// Listen for clear-conversation requests from the browser.
		_connection.On<string, string>("ClearConversation", (fromTicketId, conversationId) =>
		{
			Console.WriteLine($"Worker: ClearConversation received for ticket {fromTicketId}, conversation {conversationId}");
			if (fromTicketId == _ticketId)
			{
				_pendingClearRequests[conversationId] = true;
			}
		});

		// Listen for interrupt requests from the browser.
		// Cancels the per-conversation CTS so the LlmService loop catches OperationCanceledException.
		_connection.On<string, string>("InterruptConversation", (fromTicketId, conversationId) =>
		{
			if (fromTicketId == _ticketId)
			{
				if (_conversationInterruptSources.TryGetValue(conversationId, out CancellationTokenSource? cts))
				{
					cts.Cancel();
				}
			}
		});

		// Listen for settings updates pushed from the server.
		_connection.On<List<LLMConfig>>("SettingsUpdated", (llmConfigs) =>
		{
			_pendingSettingsUpdates.Enqueue(llmConfigs);
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

	// Atomically checks and consumes a pending clear request for a conversation.
	public bool TryConsumeClearRequest(string conversationId)
	{
		return _pendingClearRequests.TryRemove(conversationId, out _);
	}

	// Returns true if there are pending chat messages or a clear request for the conversation.
	public bool HasPendingWork(string conversationId)
	{
		if (_pendingClearRequests.ContainsKey(conversationId))
		{
			return true;
		}

		if (_pendingChatMessages.TryGetValue(conversationId, out ConcurrentQueue<string>? queue) && !queue.IsEmpty)
		{
			return true;
		}

		return false;
	}

	public ConcurrentQueue<List<LLMConfig>> GetSettingsQueue()
	{
		return _pendingSettingsUpdates;
	}

	public ConcurrentQueue<Ticket> GetTicketUpdateQueue()
	{
		return _pendingTicketUpdates;
	}

	// Registers a per-conversation CancellationTokenSource linked to the parent token.
	// Cancelling the returned token cascades to all child conversations linked to it.
	public CancellationToken RegisterConversation(string conversationId, CancellationToken parentToken)
	{
		CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
		_conversationInterruptSources[conversationId] = cts;
		return cts.Token;
	}

	// Removes and disposes the per-conversation CTS.
	public void UnregisterConversation(string conversationId)
	{
		if (_conversationInterruptSources.TryRemove(conversationId, out CancellationTokenSource? cts))
		{
			cts.Dispose();
		}
	}

	public async Task SetConversationBusyAsync(string conversationId, bool isBusy)
	{
		await HubSendAsync("SetConversationBusy", [_ticketId, conversationId, isBusy]);
	}

	public async Task SendHeartbeatAsync()
	{
		await HubSendAsync("WorkerHeartbeat", [_ticketId]);
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

	// Notifies the server and clients that a conversation was reset.
	public async Task ResetConversationAsync(string conversationId)
	{
		await HubSendAsync("ResetConversation", [_ticketId, conversationId]);
	}

	// Sends a message to the hub with retry and reconnect logic.
	// Uses InvokeAsync so the server acknowledges receipt. Falls back
	// to SendCoreAsync only if InvokeAsync times out.
	private async Task HubSendAsync(string method, object?[] args)
	{
		for (int attempt = 0; attempt < 5; attempt++)
		{
			await EnsureConnectedAsync();

			if (_connection.State != HubConnectionState.Connected)
			{
				Console.WriteLine($"Hub {method}: not connected (state={_connection.State}), attempt {attempt + 1}");
				await Task.Delay(1000);
				continue;
			}

			try
			{
				using CancellationTokenSource cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
				await _connection.InvokeCoreAsync(method, args, cts.Token);
				return;
			}
			catch (OperationCanceledException)
			{
				Console.WriteLine($"Hub {method}: InvokeAsync timed out, attempt {attempt + 1}");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Hub {method}: attempt {attempt + 1} failed: {ex.Message}");
			}

			await Task.Delay(500 * (attempt + 1));
		}

		Console.WriteLine($"Hub {method}: all 5 attempts failed, message lost");
	}

	// Waits for the connection to become active. If auto-reconnect is running,
	// polls up to 15 seconds. If fully disconnected, restarts it manually.
	private async Task EnsureConnectedAsync()
	{
		if (_connection.State == HubConnectionState.Connected)
		{
			return;
		}

		// If auto-reconnect is in progress, give it time.
		if (_connection.State == HubConnectionState.Reconnecting || _connection.State == HubConnectionState.Connecting)
		{
			for (int i = 0; i < 30; i++)
			{
				await Task.Delay(500);
				if (_connection.State == HubConnectionState.Connected)
				{
					return;
				}
			}
		}

		// Fully disconnected — restart manually.
		if (_connection.State == HubConnectionState.Disconnected)
		{
			try
			{
				Console.WriteLine("Hub connection lost, restarting...");
				await _connection.StartAsync();
				await _connection.InvokeAsync("RegisterWorker", _ticketId);
				Console.WriteLine("Hub connection re-established");
			}
			catch (Exception ex)
			{
				Console.WriteLine($"Hub reconnect failed: {ex.Message}");
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
