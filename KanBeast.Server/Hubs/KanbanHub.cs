using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Models;
using KanBeast.Server.Services;

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

    // Called by a worker to register a new conversation.
    public async Task RegisterConversation(string ticketId, string conversationId, string displayName)
    {
        _conversationStore.Register(ticketId, conversationId, displayName);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
    }

    // Called by a worker to append new messages to a conversation.
    public async Task AppendConversationMessages(string ticketId, string conversationId, List<ConversationMessage> messages)
    {
        _conversationStore.AppendMessages(ticketId, conversationId, messages);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
        await Clients.Group($"ticket-{ticketId}").ConversationMessagesAppended(ticketId, conversationId, messages);
    }

    // Called by a worker after compaction to replace the full conversation.
    public async Task ResetConversation(string ticketId, string conversationId, List<ConversationMessage> messages)
    {
        _conversationStore.ReplaceMessages(ticketId, conversationId, messages);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
        await Clients.Group($"ticket-{ticketId}").ConversationReset(ticketId, conversationId);
    }

    // Called by a worker when a conversation is done.
    public async Task FinishConversation(string ticketId, string conversationId)
    {
        _conversationStore.Finish(ticketId, conversationId);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(ticketId);
        await Clients.Group($"ticket-{ticketId}").ConversationsUpdated(ticketId, infos);
        await Clients.Group($"ticket-{ticketId}").ConversationFinished(ticketId, conversationId);
    }

    // Called by a browser to send a chat message to the worker.
    public async Task SendChatToWorker(string ticketId, string conversationId, string message)
    {
        await Clients.Group($"worker-{ticketId}").WorkerChatMessage(ticketId, conversationId, message);
    }
}

public interface IKanbanHubClient
{
    Task TicketUpdated(Ticket ticket);
    Task TicketCreated(Ticket ticket);
    Task TicketDeleted(string ticketId);
    Task ConversationsUpdated(string ticketId, List<ConversationInfo> conversations);
    Task ConversationMessagesAppended(string ticketId, string conversationId, List<ConversationMessage> messages);
    Task ConversationReset(string ticketId, string conversationId);
    Task ConversationFinished(string ticketId, string conversationId);
    Task WorkerChatMessage(string ticketId, string conversationId, string message);
}
