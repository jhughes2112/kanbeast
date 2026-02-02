using System.Collections.Concurrent;
using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

public interface ITicketService
{
    Task<Ticket> CreateTicketAsync(Ticket ticket);
    Task<Ticket?> GetTicketAsync(string id);
    Task<IEnumerable<Ticket>> GetAllTicketsAsync();
    Task<Ticket?> UpdateTicketAsync(string id, Ticket ticket);
    Task<bool> DeleteTicketAsync(string id);
    Task<Ticket?> UpdateTicketStatusAsync(string id, TicketStatus status);
    Task<Ticket?> AddTaskToTicketAsync(string id, KanbanTask task);
    Task<Ticket?> UpdateTaskStatusAsync(string ticketId, string taskId, bool isCompleted);
    Task<Ticket?> AddActivityLogAsync(string id, string activity);
    Task<Ticket?> SetBranchNameAsync(string id, string branchName);
}

public class TicketService : ITicketService
{
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();

    public Task<Ticket> CreateTicketAsync(Ticket ticket)
    {
        _tickets[ticket.Id] = ticket;
        return Task.FromResult(ticket);
    }

    public Task<Ticket?> GetTicketAsync(string id)
    {
        _tickets.TryGetValue(id, out var ticket);
        return Task.FromResult(ticket);
    }

    public Task<IEnumerable<Ticket>> GetAllTicketsAsync()
    {
        return Task.FromResult<IEnumerable<Ticket>>(_tickets.Values.OrderBy(t => t.CreatedAt));
    }

    public Task<Ticket?> UpdateTicketAsync(string id, Ticket ticket)
    {
        if (!_tickets.ContainsKey(id))
            return Task.FromResult<Ticket?>(null);

        ticket.Id = id;
        ticket.UpdatedAt = DateTime.UtcNow;
        _tickets[id] = ticket;
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<bool> DeleteTicketAsync(string id)
    {
        return Task.FromResult(_tickets.TryRemove(id, out _));
    }

    public Task<Ticket?> UpdateTicketStatusAsync(string id, TicketStatus status)
    {
        if (!_tickets.TryGetValue(id, out var ticket))
            return Task.FromResult<Ticket?>(null);

        ticket.Status = status;
        ticket.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> AddTaskToTicketAsync(string id, KanbanTask task)
    {
        if (!_tickets.TryGetValue(id, out var ticket))
            return Task.FromResult<Ticket?>(null);

        ticket.Tasks.Add(task);
        ticket.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> UpdateTaskStatusAsync(string ticketId, string taskId, bool isCompleted)
    {
        if (!_tickets.TryGetValue(ticketId, out var ticket))
            return Task.FromResult<Ticket?>(null);

        var task = ticket.Tasks.FirstOrDefault(t => t.Id == taskId);
        if (task == null)
            return Task.FromResult<Ticket?>(null);

        task.IsCompleted = isCompleted;
        task.CompletedAt = isCompleted ? DateTime.UtcNow : null;
        ticket.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> AddActivityLogAsync(string id, string activity)
    {
        if (!_tickets.TryGetValue(id, out var ticket))
            return Task.FromResult<Ticket?>(null);

        ticket.ActivityLog.Add($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {activity}");
        ticket.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> SetBranchNameAsync(string id, string branchName)
    {
        if (!_tickets.TryGetValue(id, out var ticket))
            return Task.FromResult<Ticket?>(null);

        ticket.BranchName = branchName;
        ticket.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult<Ticket?>(ticket);
    }
}
