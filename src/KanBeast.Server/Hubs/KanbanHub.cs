using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Models;

namespace KanBeast.Server.Hubs;

public class KanbanHub : Hub<IKanbanHubClient>
{
    public async Task SubscribeToTicket(string ticketId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
    }

    public async Task UnsubscribeFromTicket(string ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
    }
}

public interface IKanbanHubClient
{
    Task TicketUpdated(Ticket ticket);
    Task TicketCreated(Ticket ticket);
    Task TicketDeleted(string ticketId);
}
