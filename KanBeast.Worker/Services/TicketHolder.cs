using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

// Holds the current ticket state. Tools update this when they receive fresh data from the server.
public class TicketHolder
{
    public TicketDto Ticket { get; private set; }

    public TicketHolder(TicketDto ticket)
    {
        Ticket = ticket;
    }

    public void Update(TicketDto? newTicket)
    {
        if (newTicket != null)
        {
            Ticket = newTicket;
        }
    }
}
