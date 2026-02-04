using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    Task<Ticket?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask);
    Task<Ticket?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status);
    Task<Ticket?> MarkTaskCompleteAsync(string ticketId, string taskId);
    Task<Ticket?> AddActivityLogAsync(string id, string activity);
    Task<Ticket?> SetBranchNameAsync(string id, string branchName);
}

public class TicketService : ITicketService
{
    private readonly ConcurrentDictionary<string, Ticket> _tickets = new();
    private readonly string _ticketsDirectory;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<TicketService> _logger;
    private readonly object _idLock = new();
    private int _nextTicketId = 1;

    public TicketService(IWebHostEnvironment environment, ILogger<TicketService> logger)
    {
        _logger = logger;

        // Use /app/env in Docker, or ContentRootPath/env locally
        string basePath = Directory.Exists("/app/env") ? "/app" : environment.ContentRootPath;
        _ticketsDirectory = Path.Combine(basePath, "env", "tickets");
        Directory.CreateDirectory(_ticketsDirectory);

        _logger.LogInformation("Tickets directory: {Path}", _ticketsDirectory);

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());

        LoadTicketsFromDisk();
    }

    private void LoadTicketsFromDisk()
    {
        int maxId = 0;
        int loadedCount = 0;

        foreach (string filePath in Directory.EnumerateFiles(_ticketsDirectory, "*.json"))
        {
            try
            {
                string json = File.ReadAllText(filePath);
                Ticket? ticket = JsonSerializer.Deserialize<Ticket>(json, _jsonOptions);

                if (ticket != null && !string.IsNullOrEmpty(ticket.Id))
                {
                    _tickets[ticket.Id] = ticket;
                    loadedCount++;

                    if (int.TryParse(ticket.Id, out int ticketIdNum) && ticketIdNum > maxId)
                    {
                        maxId = ticketIdNum;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to load ticket from {Path}: {Error}", filePath, ex.Message);
            }
        }

        _nextTicketId = maxId + 1;
        _logger.LogInformation("Loaded {Count} tickets from disk, next ID: {NextId}", loadedCount, _nextTicketId);
    }

    private async Task SaveTicketToDiskAsync(Ticket ticket)
    {
        string filePath = Path.Combine(_ticketsDirectory, $"ticket-{ticket.Id}.json");
        string json = JsonSerializer.Serialize(ticket, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    private void DeleteTicketFromDisk(string id)
    {
        string filePath = Path.Combine(_ticketsDirectory, $"ticket-{id}.json");

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private string AllocateNextId()
    {
        lock (_idLock)
        {
            int id = _nextTicketId;
            _nextTicketId++;
            return id.ToString();
        }
    }

    public async Task<Ticket> CreateTicketAsync(Ticket ticket)
    {
        ticket.Id = AllocateNextId();
        _tickets[ticket.Id] = ticket;
        await SaveTicketToDiskAsync(ticket);
        return ticket;
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

    public async Task<Ticket?> UpdateTicketAsync(string id, Ticket ticket)
    {
        if (!_tickets.ContainsKey(id))
        {
            return null;
        }

        ticket.Id = id;
        ticket.UpdatedAt = DateTime.UtcNow;
        _tickets[id] = ticket;
        await SaveTicketToDiskAsync(ticket);
        return ticket;
    }

    public async Task<Ticket?> MarkTaskCompleteAsync(string ticketId, string taskName)
    {
        Ticket? ticket = null;

        if (_tickets.TryGetValue(ticketId, out Ticket? foundTicket))
        {
            ticket = foundTicket;

            KanbanTask? task = null;
            foreach (KanbanTask candidate in ticket.Tasks)
            {
                if (string.Equals(candidate.Name, taskName, StringComparison.Ordinal) ||
                    string.Equals(candidate.Id, taskName, StringComparison.Ordinal))
                {
                    task = candidate;
                    break;
                }
            }

            if (task != null)
            {
                bool allComplete = true;
                foreach (KanbanSubtask subtask in task.Subtasks)
                {
                    if (subtask.Status != SubtaskStatus.Complete)
                    {
                        allComplete = false;
                        break;
                    }
                }

                if (allComplete)
                {
                    task.LastUpdatedAt = DateTime.UtcNow;
                    ticket.UpdatedAt = DateTime.UtcNow;
                    await SaveTicketToDiskAsync(ticket);
                }
                else
                {
                    ticket = null;
                }
            }
            else
            {
                ticket = null;
            }
        }

        return ticket;
    }

    public Task<bool> DeleteTicketAsync(string id)
    {
        bool removed = _tickets.TryRemove(id, out _);

        if (removed)
        {
            DeleteTicketFromDisk(id);
        }

        return Task.FromResult(removed);
    }

    public async Task<Ticket?> UpdateTicketStatusAsync(string id, TicketStatus status)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return null;
        }

        ticket.Status = status;
        ticket.UpdatedAt = DateTime.UtcNow;
        await SaveTicketToDiskAsync(ticket);
        return ticket;
    }

    public async Task<Ticket?> AddTaskToTicketAsync(string id, KanbanTask task)
    {
        Ticket? ticket = null;

        if (_tickets.TryGetValue(id, out Ticket? foundTicket))
        {
            ticket = foundTicket;

            if (!string.IsNullOrWhiteSpace(task.Name))
            {
                KanbanTask? existingTask = null;
                foreach (KanbanTask candidate in ticket.Tasks)
                {
                    if (string.Equals(candidate.Name, task.Name, StringComparison.Ordinal))
                    {
                        existingTask = candidate;
                        break;
                    }
                }

                if (existingTask != null)
                {
                    existingTask.Description = task.Description;
                    existingTask.LastUpdatedAt = DateTime.UtcNow;
                }
                else
                {
                    task.Id = Guid.NewGuid().ToString("N");
                    task.LastUpdatedAt = DateTime.UtcNow;

                    foreach (KanbanSubtask subtask in task.Subtasks)
                    {
                        subtask.Id = Guid.NewGuid().ToString("N");
                        subtask.LastUpdatedAt = DateTime.UtcNow;
                    }

                    ticket.Tasks.Add(task);
                }

                ticket.UpdatedAt = DateTime.UtcNow;
                await SaveTicketToDiskAsync(ticket);
            }
        }

        return ticket;
    }

    public async Task<Ticket?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask)
    {
        Ticket? ticket = null;

        if (_tickets.TryGetValue(ticketId, out Ticket? foundTicket))
        {
            ticket = foundTicket;

            if (!string.IsNullOrWhiteSpace(taskId) && !string.IsNullOrWhiteSpace(subtask.Name))
            {
                KanbanTask? task = null;
                foreach (KanbanTask candidate in ticket.Tasks)
                {
                    if (string.Equals(candidate.Id, taskId, StringComparison.Ordinal))
                    {
                        task = candidate;
                        break;
                    }
                }

                if (task != null)
                {
                    KanbanSubtask? existingSubtask = null;
                    foreach (KanbanSubtask candidate in task.Subtasks)
                    {
                        if (string.Equals(candidate.Name, subtask.Name, StringComparison.Ordinal))
                        {
                            existingSubtask = candidate;
                            break;
                        }
                    }

                    if (existingSubtask != null)
                    {
                        existingSubtask.Description = subtask.Description;
                        existingSubtask.LastUpdatedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        subtask.Id = Guid.NewGuid().ToString("N");
                        subtask.LastUpdatedAt = DateTime.UtcNow;
                        task.Subtasks.Add(subtask);
                    }

                    task.LastUpdatedAt = DateTime.UtcNow;
                    ticket.UpdatedAt = DateTime.UtcNow;
                    await SaveTicketToDiskAsync(ticket);
                }
                else
                {
                    ticket = null;
                }
            }
        }

        return ticket;
    }

    public async Task<Ticket?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status)
    {
        if (!_tickets.TryGetValue(ticketId, out Ticket? ticket))
        {
            return null;
        }

        KanbanTask? task = null;
        foreach (KanbanTask candidate in ticket.Tasks)
        {
            if (string.Equals(candidate.Name, taskId, StringComparison.Ordinal) ||
                string.Equals(candidate.Id, taskId, StringComparison.Ordinal))
            {
                task = candidate;
                break;
            }
        }

        if (task == null)
        {
            return null;
        }

        KanbanSubtask? subtask = null;
        foreach (KanbanSubtask candidate in task.Subtasks)
        {
            if (string.Equals(candidate.Name, subtaskId, StringComparison.Ordinal) ||
                string.Equals(candidate.Id, subtaskId, StringComparison.Ordinal))
            {
                subtask = candidate;
                break;
            }
        }

        if (subtask == null)
        {
            return null;
        }

        subtask.Status = status;
        subtask.LastUpdatedAt = DateTime.UtcNow;
        task.LastUpdatedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;
        await SaveTicketToDiskAsync(ticket);
        return ticket;
    }

    public async Task<Ticket?> AddActivityLogAsync(string id, string activity)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return null;
        }

        ticket.ActivityLog.Add($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {activity}");
        ticket.UpdatedAt = DateTime.UtcNow;
        await SaveTicketToDiskAsync(ticket);
        return ticket;
    }

    public async Task<Ticket?> SetBranchNameAsync(string id, string branchName)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return null;
        }

        ticket.BranchName = branchName;
        ticket.UpdatedAt = DateTime.UtcNow;
        await SaveTicketToDiskAsync(ticket);
        return ticket;
    }
}
