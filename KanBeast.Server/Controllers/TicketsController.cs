using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using KanBeast.Server.Models;
using KanBeast.Server.Services;
using KanBeast.Server.Hubs;
using KanBeast.Shared;

namespace KanBeast.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TicketsController : ControllerBase
{
    private readonly ITicketService _ticketService;
    private readonly IWorkerOrchestrator _workerOrchestrator;
    private readonly IHubContext<KanbanHub, IKanbanHubClient> _hubContext;
    private readonly ConversationStore _conversationStore;
    private readonly ILogger<TicketsController> _logger;

    public TicketsController(
        ITicketService ticketService,
        IWorkerOrchestrator workerOrchestrator,
        IHubContext<KanbanHub, IKanbanHubClient> hubContext,
        ConversationStore conversationStore,
        ILogger<TicketsController> logger)
    {
        _ticketService = ticketService;
        _workerOrchestrator = workerOrchestrator;
        _hubContext = hubContext;
        _conversationStore = conversationStore;
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

    [HttpGet("{id}/conversations")]
    public ActionResult<List<ConversationInfo>> GetConversations(string id)
    {
        List<ConversationInfo> infos = _conversationStore.GetInfoList(id);
        return Ok(infos);
    }

    [HttpGet("{id}/conversations/{conversationId}")]
    public ActionResult<ConversationData> GetConversation(string id, string conversationId)
    {
        ConversationData? data = _conversationStore.Get(id, conversationId);
        if (data == null)
        {
            return NotFound();
        }

        return Ok(data);
    }

    [HttpDelete("{id}/conversations/{conversationId}")]
    public async Task<IActionResult> DeleteConversation(string id, string conversationId)
    {
        ConversationData? data = _conversationStore.Get(id, conversationId);
        if (data == null)
        {
            return NotFound();
        }

        if (!data.IsFinished)
        {
            return BadRequest("Cannot delete an active conversation");
        }

        await _conversationStore.DeleteAsync(id, conversationId);
        List<ConversationInfo> infos = _conversationStore.GetInfoList(id);
        await _hubContext.Clients.Group($"ticket-{id}").ConversationsUpdated(id, infos);
        return NoContent();
    }

    [HttpGet("{id}/conversations/planning")]
    public ActionResult<ConversationData> GetPlanningConversation(string id)
    {
        ConversationData? data = _conversationStore.GetActivePlanning(id);
        if (data == null)
        {
            return NotFound();
        }

        return Ok(data);
    }

    [HttpPost]
    public async Task<ActionResult<Ticket>> CreateTicket([FromBody] Ticket ticket)
    {
        Ticket createdTicket = await _ticketService.CreateTicketAsync(ticket);
        _logger.LogInformation("POST /tickets - created #{Id}: {Title}", createdTicket.Id, createdTicket.Title);

        // Start a worker container for this ticket immediately.
        if (_workerOrchestrator is WorkerOrchestrator orchestrator)
        {
            try
            {
                string workerId = await _workerOrchestrator.StartWorkerAsync(createdTicket.Id);
                _logger.LogInformation("Worker container started: {WorkerId} for ticket #{Id}", workerId, createdTicket.Id);

                // Re-fetch to get the containerName and port that were set during start.
                Ticket? refreshed = await _ticketService.GetTicketAsync(createdTicket.Id);
                if (refreshed != null)
                {
                    createdTicket = refreshed;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not start worker container for ticket #{Id}: {Message}", createdTicket.Id, ex.Message);
            }
        }

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
        // Stop the worker container before deleting the ticket.
        try
        {
            bool stopped = await _workerOrchestrator.StopWorkerAsync(id);
            if (stopped)
            {
                _logger.LogInformation("Stopped worker container for deleted ticket #{Id}", id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to stop worker for ticket #{Id}: {Message}", id, ex.Message);
        }

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

    [HttpPatch("{id}/details")]
    public async Task<ActionResult<Ticket>> UpdateTitleDescription(string id, [FromBody] TicketTitleDescriptionUpdate update)
    {
        Ticket? ticket = await _ticketService.UpdateTicketTitleDescriptionAsync(id, update.Title, update.Description);
        if (ticket == null)
        {
            return NotFound();
        }

        _logger.LogInformation("PATCH /tickets/{Id}/details - updated title/description", id);
        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpDelete("{id}/tasks")]
    public async Task<ActionResult<Ticket>> DeleteAllTasks(string id)
    {
        Ticket? ticket = await _ticketService.DeleteAllTasksAsync(id);
        if (ticket == null)
        {
            return NotFound();
        }

        _logger.LogInformation("DELETE /tickets/{Id}/tasks - deleted all tasks", id);
        await _hubContext.Clients.Group($"ticket-{id}").TicketUpdated(ticket);
        await _hubContext.Clients.All.TicketUpdated(ticket);
        return Ok(ticket);
    }

    [HttpPatch("{id}/plannerllm")]
    public async Task<ActionResult<Ticket>> SetPlannerLlm(string id, [FromBody] PlannerLlmUpdate update)
    {
        Ticket? ticket = await _ticketService.SetPlannerLlmAsync(id, update.PlannerLlmId);
        if (ticket == null)
        {
            return NotFound();
        }

        _logger.LogInformation("PATCH /tickets/{Id}/plannerllm - set to {PlannerLlmId}", id, update.PlannerLlmId ?? "auto");
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
public record TicketTitleDescriptionUpdate(string Title, string Description);
public record PlannerLlmUpdate(string? PlannerLlmId);
