using System.Diagnostics;
using KanBeast.Server.Models;

namespace KanBeast.Server.Services;

public interface IWorkerOrchestrator
{
    Task<string> StartWorkerAsync(string ticketId);
    Task<bool> StopWorkerAsync(string workerId);
    Task<Dictionary<string, string>> GetActiveWorkersAsync();
}

public class WorkerOrchestrator : IWorkerOrchestrator
{
    private readonly Dictionary<string, Process> _activeWorkers = new();
    private readonly ISettingsService _settingsService;
    private readonly ITicketService _ticketService;
    private readonly ILogger<WorkerOrchestrator> _logger;

    public WorkerOrchestrator(
        ISettingsService settingsService,
        ITicketService ticketService,
        ILogger<WorkerOrchestrator> logger)
    {
        _settingsService = settingsService;
        _ticketService = ticketService;
        _logger = logger;
    }

    public async Task<string> StartWorkerAsync(string ticketId)
    {
        var ticket = await _ticketService.GetTicketAsync(ticketId);
        if (ticket == null)
            throw new InvalidOperationException($"Ticket {ticketId} not found");

        var settings = await _settingsService.GetSettingsAsync();
        var workerId = Guid.NewGuid().ToString();

        // In a real implementation, this would start a Docker container
        // For now, we'll use a placeholder that represents the worker process
        _logger.LogInformation($"Starting worker {workerId} for ticket {ticketId}");

        // Update ticket with worker ID
        ticket.WorkerId = workerId;
        await _ticketService.AddActivityLogAsync(ticketId, $"Worker {workerId} assigned");

        // Store the worker (in production, this would be a Docker container ID)
        _activeWorkers[workerId] = null!; // Placeholder for actual process

        return workerId;
    }

    public Task<bool> StopWorkerAsync(string workerId)
    {
        if (!_activeWorkers.ContainsKey(workerId))
            return Task.FromResult(false);

        _logger.LogInformation($"Stopping worker {workerId}");
        
        // In production, stop the Docker container
        _activeWorkers.Remove(workerId);
        return Task.FromResult(true);
    }

    public Task<Dictionary<string, string>> GetActiveWorkersAsync()
    {
        return Task.FromResult(_activeWorkers.Keys.ToDictionary(k => k, k => "Running"));
    }
}
