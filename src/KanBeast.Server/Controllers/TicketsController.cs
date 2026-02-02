using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Models;
using KanBeast.Server.Services;
using KanBeast.Server.Hubs;

namespace KanBeast.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _ticketService;
    private readonly IWorkerOrchestrator _workerOrchestrator;
    private readonly IHubContext<KanbanHub, IKanbanHubClient> _hubContext;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(
        ITicketService ticketService,
        IWorkerOrchestrator workerOrchestrator,
        IHubContext<KanbanHub, IKanbanHubClient> hubContext,
        ILogger<TicketsController> logger)
    {
        _ticketService = ticketService;
        _workerOrchestrator = workerOrchestrator;
        _hubContext = hubContext;
        _logger = logger;
    }

    [HttpGet]
    public async Task<ActionResult<IEnumerable<Ticket>>> GetAllTickets()
    {
        var tickets = await _ticketService.GetAllTicketsAsync();
        return Ok(tickets);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Ticket>> GetTicket(string id)
    {
        var ticket = await _ticketService.GetTicketAsync(id);
        if (ticket == null)
            return NotFound();

        return Ok(ticket);
    }

    [HttpPost]
    public async Task<ActionResult<Ticket>> CreateTicket([FromBody] Ticket ticket)
    {
        var createdTicket = await _ticketService.CreateTicketAsync(ticket);
        await _hubContext.Clients.All.TicketCreated(createdTicket);
        return Ok(createdTicket);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Ticket>> UpdateTicket(string id, [FromBody] Ticket ticket)
    {
        var updatedTicket = await _ticketService.UpdateTicketAsync(id, ticket);
        if (updatedTicket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(updatedTicket);
        return Ok(updatedTicket);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteTicket(string id)
    {
        var result = await _ticketService.DeleteTicketAsync(id);
        if (!result)
            return NotFound();

        await _hubContext.Clients.All.TicketDeleted(id);
        return NoContent();
    }

    [HttpPatch("{id}/status")]
    public async Task<ActionResult<Ticket>> UpdateTicketStatus(string id, [FromBody] TicketStatusUpdate update)
    {
        var ticket = await _ticketService.UpdateTicketStatusAsync(id, update.Status);
        if (ticket == null)
            return NotFound();

        // If moving to Active, start a worker
        if (update.Status == TicketStatus.Active && string.IsNullOrEmpty(ticket.WorkerId))
        {
            try
            {
                var workerId = await _workerOrchestrator.StartWorkerAsync(id);
                _logger.LogInformation($"Started worker {workerId} for ticket {id}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to start worker for ticket {id}");
            }
        }

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPost("{id}/tasks")]
    public async Task<ActionResult<Ticket>> AddTask(string id, [FromBody] KanbanTask task)
    {
        var ticket = await _ticketService.AddTaskToTicketAsync(id, task);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{ticketId}/tasks/{taskId}")]
    public async Task<ActionResult<Ticket>> UpdateTaskStatus(string ticketId, string taskId, [FromBody] TaskStatusUpdate update)
    {
        var ticket = await _ticketService.UpdateTaskStatusAsync(ticketId, taskId, update.IsCompleted);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{ticketId}").TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPost("{id}/activity")]
    public async Task<ActionResult<Ticket>> AddActivity(string id, [FromBody] ActivityUpdate activity)
    {
        var ticket = await _ticketService.AddActivityLogAsync(id, activity.Message);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id}/branch")]
    public async Task<ActionResult<Ticket>> SetBranch(string id, [FromBody] BranchUpdate update)
    {
        var ticket = await _ticketService.SetBranchNameAsync(id, update.BranchName);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        return Ok(ticket);
    }
}

public record TicketStatusUpdate(TicketStatus Status);
public record TaskStatusUpdate(bool IsCompleted);
public record ActivityUpdate(string Message);
public record BranchUpdate(string BranchName);
