using KanBeast.Shared;

namespace KanBeast.Worker.Services;

// Holds the current ticket state. Tools update this when they receive fresh data from the server.
public class TicketHolder
{
    public Ticket Ticket { get; private set; }

    public TicketHolder(Ticket ticket)
    {
        Ticket = ticket;
    }

    public void Update(Ticket? newTicket)
    {
        if (newTicket != null)
        {
            Ticket = newTicket;
        }
    }

    public string? FindTaskIdByName(string taskName)
    {
        foreach (KanbanTask task in Ticket.Tasks)
        {
            if (string.Equals(task.Name, taskName, StringComparison.Ordinal))
            {
                return task.Id;
            }
        }

        return null;
    }
}
