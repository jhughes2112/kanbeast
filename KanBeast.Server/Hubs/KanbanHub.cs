using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Models;
using KanBeast.Server.Services;
using KanBeast.Shared;

namespace KanBeast.Server.Hubs;

public class KanbanHub : Hub<IKanbanHubClient>
{
    // Tracks which conversations are currently busy (LLM running) per ticket.
    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> BusyState = new();

    // Tracks the last heartbeat time per ticket from its worker.
    private static readonly ConcurrentDictionary<string, DateTimeOffset> WorkerHeartbeats = new();

    private readonly ConversationStore _conversationStore;

    public KanbanHub(ConversationStore conversationStore)
    {
        _conversationStore = conversationStore;
    }

    // Returns the last heartbeat time for all tracked tickets.
    public static IReadOnlyDictionary<string, DateTimeOffset> GetWorkerHeartbeats()
    {
        return WorkerHeartbeats;
    }

    // Removes the heartbeat entry for a ticket (called when the ticket leaves Active).
    public static void ClearHeartbeat(string ticketId)
    {
        WorkerHeartbeats.TryRemove(ticketId, out _);
    }

    public async Task SubscribeToTicket(string ticketId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");

        // Replay current busy states so the caller knows which conversations are active.
        if (BusyState.TryGetValue(ticketId, out ConcurrentDictionary<string, bool>? convos))
        {
            foreach ((string conversationId, bool isBusy) in convos)
            {
                if (isBusy)
                {
                    await Clients.Caller.ConversationBusy(ticketId, conversationId, true);
                }
            }
        }
    }

    public async Task UnsubscribeFromTicket(string ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
    }

    // Called by a worker to register itself and receive chat messages for its ticket.
    public async Task RegisterWorker(string ticketId)
    {
        Console.WriteLine($"Hub: RegisterWorker called for ticket '{ticketId}', connectionId: {Context.ConnectionId}");
        WorkerHeartbeats[ticketId] = DateTimeOffset.UtcNow;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"worker-{ticketId}");
        Console.WriteLine($"Hub: Worker registered in groups ticket-{ticketId} and worker-{ticketId}");
    }

    // Called periodically by a worker to signal it is still alive.
    public void WorkerHeartbeat(string ticketId)
    {
        WorkerHeartbeats[ticketId] = DateTimeOffset.UtcNow;
    }

    // Called by a worker to push the full conversation snapshot.
    public async Task SyncConversation(string ticketId, ConversationData data)
    {
        WorkerHeartbeats[ticketId] = DateTimeOffset.UtcNow;
        await _conversationStore.UpsertAsync(ticketId, data);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
        await Clients.Group($"ticket-{ticketId}").ConversationSynced(ticketId, data.Id);
    }

    // Called by a worker when a conversation is done.
    public async Task FinishConversation(string ticketId, string conversationId)
    {
        await _conversationStore.FinishAsync(ticketId, conversationId);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
        await Clients.Group($"ticket-{ticketId}").ConversationFinished(ticketId, conversationId);
    }

    // Called by a worker after it resets a conversation back to its initial state.
    public async Task ResetConversation(string ticketId, string conversationId)
    {
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
        await Clients.Group($"ticket-{ticketId}").ConversationReset(ticketId, conversationId);
    }

    // Called by a browser to send a chat message to the worker.
    public async Task SendChatToWorker(string ticketId, string conversationId, string message)
    {
        Console.WriteLine($"Hub: SendChatToWorker for ticket '{ticketId}' to group worker-{ticketId}");
        await Clients.Group($"worker-{ticketId}").WorkerChatMessage(ticketId, conversationId, message);
    }

    // Called by a browser to request the worker clear a conversation back to its initial state.
    public async Task RequestClearConversation(string ticketId, string conversationId)
    {
        Console.WriteLine($"Hub: RequestClearConversation received for ticket {ticketId}, conversation {conversationId}");
        await Clients.Group($"worker-{ticketId}").ClearConversation(ticketId, conversationId);
    }

    // Called by a worker to signal that a conversation is busy (LLM running) or idle.
    public async Task SetConversationBusy(string ticketId, string conversationId, bool isBusy)
    {
        WorkerHeartbeats[ticketId] = DateTimeOffset.UtcNow;
        ConcurrentDictionary<string, bool> convos = BusyState.GetOrAdd(ticketId, _ => new ConcurrentDictionary<string, bool>());
        if (isBusy)
        {
            convos[conversationId] = true;
        }
        else
        {
            convos.TryRemove(conversationId, out _);
        }

        await Clients.Group($"ticket-{ticketId}").ConversationBusy(ticketId, conversationId, isBusy);
    }

    // Called by a browser to request the worker interrupt the current LLM operation.
    public async Task RequestInterruptConversation(string ticketId, string conversationId)
    {
        await Clients.Group($"worker-{ticketId}").InterruptConversation(ticketId, conversationId);
    }

    // Called by a browser to change the LLM model of a running conversation.
    public async Task ChangeConversationModel(string ticketId, string conversationId, string llmConfigId)
    {
        Console.WriteLine($"Hub: ChangeConversationModel for ticket '{ticketId}', conversation '{conversationId}', llm '{llmConfigId}'");
        await Clients.Group($"worker-{ticketId}").ConversationModelChanged(ticketId, conversationId, llmConfigId);
    }
}

public interface IKanbanHubClient
{
    Task TicketUpdated(Ticket ticket);
    Task TicketCreated(Ticket ticket);
    Task TicketDeleted(string ticketId);
    Task ConversationsUpdated(string ticketId, List<ConversationInfo> conversations);
    Task ConversationSynced(string ticketId, string conversationId);
    Task ConversationFinished(string ticketId, string conversationId);
    Task ConversationReset(string ticketId, string conversationId);
    Task ConversationBusy(string ticketId, string conversationId, bool isBusy);
    Task ClearConversation(string ticketId, string conversationId);
    Task InterruptConversation(string ticketId, string conversationId);
    Task ConversationModelChanged(string ticketId, string conversationId, string llmConfigId);
    Task SettingsUpdated(SettingsFile settingsFile);
    Task WorkerChatMessage(string ticketId, string conversationId, string message);
}
