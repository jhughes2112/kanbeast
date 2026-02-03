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
    Task<Ticket?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status);
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

    private void SaveTicketToDisk(Ticket ticket)
    {
        string filePath = Path.Combine(_ticketsDirectory, $"ticket-{ticket.Id}.json");
        string json = JsonSerializer.Serialize(ticket, _jsonOptions);
        File.WriteAllText(filePath, json);
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

    public Task<Ticket> CreateTicketAsync(Ticket ticket)
    {
        ticket.Id = AllocateNextId();
        _tickets[ticket.Id] = ticket;
        SaveTicketToDisk(ticket);
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
        {
            return Task.FromResult<Ticket?>(null);
        }

        ticket.Id = id;
        ticket.UpdatedAt = DateTime.UtcNow;
        _tickets[id] = ticket;
        SaveTicketToDisk(ticket);
        return Task.FromResult<Ticket?>(ticket);
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

    public Task<Ticket?> UpdateTicketStatusAsync(string id, TicketStatus status)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return Task.FromResult<Ticket?>(null);
        }

        ticket.Status = status;
        ticket.UpdatedAt = DateTime.UtcNow;
        SaveTicketToDisk(ticket);
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> AddTaskToTicketAsync(string id, KanbanTask task)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return Task.FromResult<Ticket?>(null);
        }

        task.LastUpdatedAt = DateTime.UtcNow;
        foreach (KanbanSubtask subtask in task.Subtasks)
        {
            subtask.LastUpdatedAt = DateTime.UtcNow;
        }

        ticket.Tasks.Add(task);
        ticket.UpdatedAt = DateTime.UtcNow;
        SaveTicketToDisk(ticket);
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status)
    {
        if (!_tickets.TryGetValue(ticketId, out Ticket? ticket))
        {
            return Task.FromResult<Ticket?>(null);
        }

        KanbanTask? task = null;
        foreach (KanbanTask candidate in ticket.Tasks)
        {
            if (string.Equals(candidate.Id, taskId, StringComparison.Ordinal))
            {
                task = candidate;
                break;
            }
        }

        if (task == null)
        {
            return Task.FromResult<Ticket?>(null);
        }

        KanbanSubtask? subtask = null;
        foreach (KanbanSubtask candidate in task.Subtasks)
        {
            if (string.Equals(candidate.Id, subtaskId, StringComparison.Ordinal))
            {
                subtask = candidate;
                break;
            }
        }

        if (subtask == null)
        {
            return Task.FromResult<Ticket?>(null);
        }

        subtask.Status = status;
        subtask.LastUpdatedAt = DateTime.UtcNow;
        task.LastUpdatedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;
        SaveTicketToDisk(ticket);
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> AddActivityLogAsync(string id, string activity)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return Task.FromResult<Ticket?>(null);
        }

        ticket.ActivityLog.Add($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] {activity}");
        ticket.UpdatedAt = DateTime.UtcNow;
        SaveTicketToDisk(ticket);
        return Task.FromResult<Ticket?>(ticket);
    }

    public Task<Ticket?> SetBranchNameAsync(string id, string branchName)
    {
        if (!_tickets.TryGetValue(id, out Ticket? ticket))
        {
            return Task.FromResult<Ticket?>(null);
        }

        ticket.BranchName = branchName;
        ticket.UpdatedAt = DateTime.UtcNow;
        SaveTicketToDisk(ticket);
        return Task.FromResult<Ticket?>(ticket);
    }
}
