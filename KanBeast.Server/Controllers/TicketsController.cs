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
        IEnumerable<Ticket> tickets = await _ticketService.GetAllTicketsAsync();
        _logger.LogInformation("GET /tickets - returning {Count} tickets", tickets.Count());
        return Ok(tickets);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Ticket>> GetTicket(string id)
    {
        Ticket? ticket = await _ticketService.GetTicketAsync(id);
        if (ticket == null)
        {
            _logger.LogWarning("GET /tickets/{Id} - not found", id);
            return NotFound();
        }

        return Ok(ticket);
    }

    [HttpPost]
    public async Task<ActionResult<Ticket>> CreateTicket([FromBody] Ticket ticket)
    {
        Ticket createdTicket = await _ticketService.CreateTicketAsync(ticket);
        _logger.LogInformation("POST /tickets - created #{Id}: {Title}", createdTicket.Id, createdTicket.Title);
        await _hubContext.Clients.All.TicketCreated(createdTicket);
        return Ok(createdTicket);
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<Ticket>> UpdateTicket(string id, [FromBody] Ticket ticket)
    {
        Ticket? updatedTicket = await _ticketService.UpdateTicketAsync(id, ticket);
        if (updatedTicket == null)
        {
            return NotFound();
        }

        _logger.LogInformation("PUT /tickets/{Id} - updated", id);
        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(updatedTicket);
        await _hubContext.Clients.All.TicketUpdated(updatedTicket);
        return Ok(updatedTicket);
    }

    [HttpDelete("{id}")]
    public async Task<ActionResult> DeleteTicket(string id)
    {
        bool result = await _ticketService.DeleteTicketAsync(id);
        if (!result)
        {
            return NotFound();
        }

        _logger.LogInformation("DELETE /tickets/{Id} - deleted", id);
        await _hubContext.Clients.All.TicketDeleted(id);
        return NoContent();
    }

    [HttpPatch("{id}/status")]
    public async Task<ActionResult<Ticket>> UpdateTicketStatus(string id, [FromBody] TicketStatusUpdate update)
    {
        Ticket? ticket = await _ticketService.UpdateTicketStatusAsync(id, update.Status);
        if (ticket == null)
        {
            return NotFound();
        }

        _logger.LogInformation("PATCH /tickets/{Id}/status - changed to {Status}", id, update.Status);

        // If moving to Backlog, stop any running worker
        if (update.Status == TicketStatus.Backlog)
        {
            try
            {
                bool stopped = await _workerOrchestrator.StopWorkerAsync(id);
                if (stopped)
                {
                    _logger.LogInformation("Stopped worker for cancelled ticket #{Id}", id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop worker for ticket #{Id}: {Message}", id, ex.Message);
            }
        }

        // If moving to Active, start a worker
        if (update.Status == TicketStatus.Active)
        {
            try
            {
                _logger.LogInformation("Starting worker for ticket #{Id}...", id);
                string workerId = await _workerOrchestrator.StartWorkerAsync(id);
                _logger.LogInformation("Worker started: {WorkerId} for ticket #{Id}", workerId, id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start worker for ticket #{Id}: {Message}", id, ex.Message);
            }
        }

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPost("{id}/tasks")]
    public async Task<ActionResult<Ticket>> AddTask(string id, [FromBody] KanbanTask task)
    {
        ActionResult<Ticket> result;
        Ticket? ticket = await _ticketService.AddTaskToTicketAsync(id, task);

        if (ticket == null)
        {
            result = NotFound();
        }
        else
        {
            await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
            await _hubContext.Clients.All.TicketUpdated(ticket);
            result = Ok(ticket);
        }

        return result;
    }

    [HttpPatch("{ticketId}/tasks/{taskName}/complete")]
    public async Task<ActionResult<Ticket>> MarkTaskComplete(string ticketId, string taskName)
    {
        ActionResult<Ticket> result;
        Ticket? ticket = await _ticketService.MarkTaskCompleteAsync(ticketId, taskName);

        if (ticket == null)
        {
            result = NotFound();
        }
        else
        {
            await _hubContext.Clients.Group($"ticket-{ticketId}").TicketUpdated(ticket);
            await _hubContext.Clients.All.TicketUpdated(ticket);
            result = Ok(ticket);
        }

        return result;
    }

    [HttpPost("{id}/tasks/{taskId}/subtasks")]
    public async Task<ActionResult<Ticket>> AddSubtask(string id, string taskId, [FromBody] KanbanSubtask subtask)
    {
        ActionResult<Ticket> result;
        Ticket? ticket = await _ticketService.AddSubtaskToTaskAsync(id, taskId, subtask);

        if (ticket == null)
        {
            result = NotFound();
        }
        else
        {
            await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
            await _hubContext.Clients.All.TicketUpdated(ticket);
            result = Ok(ticket);
        }

        return result;
    }

    [HttpPatch("{ticketId}/tasks/{taskId}/subtasks/{subtaskId}")]
    public async Task<ActionResult<Ticket>> UpdateSubtaskStatus(string ticketId, string taskId, string subtaskId, [FromBody] SubtaskStatusUpdate update)
    {
        ActionResult<Ticket> result;
        Ticket? ticket = await _ticketService.UpdateSubtaskStatusAsync(ticketId, taskId, subtaskId, update.Status);

        if (ticket == null)
        {
            result = NotFound();
        }
        else
        {
            await _hubContext.Clients.Group($"ticket-{ticketId}").TicketUpdated(ticket);
            await _hubContext.Clients.All.TicketUpdated(ticket);
            result = Ok(ticket);
        }

        return result;
    }

    [HttpPost("{id}/activity")]
    public async Task<ActionResult<Ticket>> AddActivity(string id, [FromBody] ActivityUpdate activity)
    {
        var ticket = await _ticketService.AddActivityLogAsync(id, activity.Message);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id}/branch")]
    public async Task<ActionResult<Ticket>> SetBranch(string id, [FromBody] BranchUpdate update)
    {
        var ticket = await _ticketService.SetBranchNameAsync(id, update.BranchName);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id}/cost")]
    public async Task<ActionResult<Ticket>> AddCost(string id, [FromBody] CostUpdate update)
    {
        var ticket = await _ticketService.AddLlmCostAsync(id, update.Cost);
        if (ticket == null)
            return NotFound();

        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id}/maxcost")]
    public async Task<ActionResult<Ticket>> SetMaxCost(string id, [FromBody] MaxCostUpdate update)
    {
        Ticket? ticket = await _ticketService.SetMaxCostAsync(id, update.MaxCost);
        if (ticket == null)
        {
            return NotFound();
        }

        _logger.LogInformation("PATCH /tickets/{Id}/maxcost - set to ${MaxCost:F2}", id, update.MaxCost);
        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }
}

public record TicketStatusUpdate(TicketStatus Status);
public record SubtaskStatusUpdate(SubtaskStatus Status);
public record ActivityUpdate(string Message);
public record BranchUpdate(string BranchName);
public record CostUpdate(decimal Cost);
public record MaxCostUpdate(decimal MaxCost);
