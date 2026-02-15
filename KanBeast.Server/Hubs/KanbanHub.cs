using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Models;
using KanBeast.Server.Services;
using KanBeast.Shared;

namespace KanBeast.Server.Hubs;

public class KanbanHub : Hub<IKanbanHubClient>
{
    private readonly ConversationStore _conversationStore;

    public KanbanHub(ConversationStore conversationStore)
    {
        _conversationStore = conversationStore;
    }

    public async Task SubscribeToTicket(string ticketId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
    }

    public async Task UnsubscribeFromTicket(string ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
    }

    // Called by a worker to register itself and receive chat messages for its ticket.
    public async Task RegisterWorker(string ticketId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
        await Groups.AddToGroupAsync(Context.ConnectionId, $"worker-{ticketId}");
    }

    // Called by a worker to push the full conversation snapshot.
    public async Task SyncConversation(string ticketId, ConversationData data)
    {
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
        await Clients.Group($"worker-{ticketId}").WorkerChatMessage(ticketId, conversationId, message);
    }

    // Called by a browser to request the worker clear a conversation back to its initial state.
    public async Task RequestClearConversation(string ticketId, string conversationId)
    {
        await Clients.Group($"worker-{ticketId}").ClearConversation(ticketId, conversationId);
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
    Task ClearConversation(string ticketId, string conversationId);
    Task SettingsUpdated(List<LLMConfig> llmConfigs);
    Task WorkerChatMessage(string ticketId, string conversationId, string message);
}
