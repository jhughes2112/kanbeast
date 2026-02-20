using KanBeast.Server.Hubs;
using KanBeast.Shared;
using Microsoft.AspNetCore.SignalR;

namespace KanBeast.Server.Services;

// Periodically checks Active tickets for stale workers and moves them to Failed.
// A ticket is considered stale if its worker has not sent a heartbeat in 5 minutes.
// Workers send heartbeats every LLM iteration, so a 5-minute gap means the worker
// is genuinely dead (crash, OOM, network partition), not just busy.
public class ActiveTicketWatchdog : BackgroundService
{
	private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

	private readonly ITicketService _ticketService;
	private readonly IHubContext<KanbanHub, IKanbanHubClient> _hubContext;
	private readonly ILogger<ActiveTicketWatchdog> _logger;

	public ActiveTicketWatchdog(
		ITicketService ticketService,
		IHubContext<KanbanHub, IKanbanHubClient> hubContext,
		ILogger<ActiveTicketWatchdog> logger)
	{
		_ticketService = ticketService;
		_hubContext = hubContext;
		_logger = logger;
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		while (!stoppingToken.IsCancellationRequested)
		{
			await Task.Delay(CheckInterval, stoppingToken);

			try
			{
				await CheckForStaleTicketsAsync();
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				_logger.LogError(ex, "Error checking for stale tickets");
			}
		}
	}

	private async Task CheckForStaleTicketsAsync()
	{
		IEnumerable<Ticket> tickets = await _ticketService.GetAllTicketsAsync();
		IReadOnlyDictionary<string, DateTimeOffset> heartbeats = KanbanHub.GetWorkerHeartbeats();
		DateTimeOffset now = DateTimeOffset.UtcNow;

		foreach (Ticket ticket in tickets)
		{
			if (ticket.Status != TicketStatus.Active)
			{
				continue;
			}

			if (!heartbeats.TryGetValue(ticket.Id, out DateTimeOffset lastHeartbeat))
			{
				continue;
			}

			TimeSpan elapsed = now - lastHeartbeat;
			if (elapsed <= StaleThreshold)
			{
				continue;
			}

			_logger.LogWarning("Ticket #{TicketId} worker stale ({Elapsed:F0}s since last heartbeat), moving to Failed", ticket.Id, elapsed.TotalSeconds);

			await _ticketService.AddActivityLogAsync(ticket.Id, $"Watchdog: Worker unresponsive for {elapsed.TotalSeconds:F0}s, marking as Failed");
			Ticket? updated = await _ticketService.UpdateTicketStatusAsync(ticket.Id, TicketStatus.Failed);

			if (updated != null)
			{
				await _hubContext.Clients.All.TicketUpdated(updated);
			}

			KanbanHub.ClearHeartbeat(ticket.Id);
		}
	}
}
