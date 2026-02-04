using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Phases the orchestrator transitions through when processing a ticket.
public enum OrchestratorPhase
{
    Breakdown,
    Assign,
    Develop,
    Verify,
    Finalize,
    Complete,
    Blocked
}

// Orchestrates the manager and developer LLMs to complete a ticket.
public interface IAgentOrchestrator
{
    Task RunAsync(TicketDto ticket, string workDir, CancellationToken cancellationToken);
}

// Coordinates LLM-driven phases: breakdown, assign, develop, verify, finalize.
public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly IKanbanApiClient _apiClient;
    private readonly LlmProxy _managerLlmService;
    private readonly LlmProxy _developerLlmService;
    private readonly string _managerPrompt;
    private readonly string _developerImplementationPrompt;
    private readonly int _maxIterationsPerSubtask;

    private OrchestratorPhase _phase;

    public AgentOrchestrator(
        ILogger<AgentOrchestrator> logger,
        IKanbanApiClient apiClient,
        LlmProxy managerLlmService,
        LlmProxy developerLlmService,
        string managerPrompt,
        string developerImplementationPrompt,
        int maxIterationsPerSubtask)
    {
        _logger = logger;
        _apiClient = apiClient;
        _managerLlmService = managerLlmService;
        _developerLlmService = developerLlmService;
        _managerPrompt = managerPrompt;
        _developerImplementationPrompt = developerImplementationPrompt;
        _maxIterationsPerSubtask = maxIterationsPerSubtask;
        _phase = OrchestratorPhase.Breakdown;
    }

    // Main entry point that runs the orchestrator loop until complete or blocked.
    public async Task RunAsync(TicketDto ticket, string workDir, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Orchestrator starting for ticket: {Title}", ticket.Title);
        await _apiClient.AddActivityLogAsync(ticket.Id, "Orchestrator: Starting work");

        TicketHolder ticketHolder = new TicketHolder(ticket);
        TaskState state = new TaskState();
        TicketTools ticketTools = new TicketTools(_apiClient, ticketHolder, state);

        List<IToolProvider> toolProviders = new List<IToolProvider>
        {
            new ShellTools(workDir),
            new FileTools(workDir),
            ticketTools
        };

        int totalIterations = 0;
        int maxTotalIterations = 500;

        for (;;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalIterations++;

            _phase = DetermineCurrentPhase(ticketHolder.Ticket, state);
            _logger.LogDebug("Phase: {Phase} (iteration {Iteration})", _phase, totalIterations);

            if (_phase == OrchestratorPhase.Complete)  // exit condition met
            {
                _logger.LogInformation("Orchestrator completed successfully");
                await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Completed successfully");
                break;
            }

            if (_phase == OrchestratorPhase.Blocked)  // exit condition met
            {
                _logger.LogWarning("Orchestrator blocked - requires human intervention");
                await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Blocked - requires human intervention");
                break;
            }

			if (totalIterations >= maxTotalIterations)  // exit condition met
			{
				_logger.LogWarning("Orchestrator exceeded max iterations ({Max})", maxTotalIterations);
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Exceeded max iterations ({maxTotalIterations})");
                break;
			}

            switch (_phase)
            {
                case OrchestratorPhase.Breakdown:
                    await RunBreakdownPhaseAsync(ticketHolder, state, toolProviders, cancellationToken);
                    break;

                case OrchestratorPhase.Assign:
                    await RunAssignPhaseAsync(ticketHolder, state, toolProviders, cancellationToken);
                    break;

                case OrchestratorPhase.Develop:
                    await RunDevelopPhaseAsync(ticketHolder, state, toolProviders, cancellationToken);
                    break;

                case OrchestratorPhase.Verify:
                    await RunVerifyPhaseAsync(ticketHolder, state, toolProviders, cancellationToken);
                    break;

                case OrchestratorPhase.Finalize:
                    await RunFinalizePhaseAsync(ticketHolder, state, toolProviders, cancellationToken);
                    break;
            }
        }
    }

    // Inspects ticket state to determine which phase should be active.
    private OrchestratorPhase DetermineCurrentPhase(TicketDto ticket, TaskState state)
    {
        if (state.Blocked)
        {
            return OrchestratorPhase.Blocked;
        }

        if (state.TicketComplete == true)
        {
            return OrchestratorPhase.Complete;
        }

        bool hasSubtasks = false;
        bool hasIncomplete = false;
        bool hasAwaitingReview = false;

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                hasSubtasks = true;

                if (subtask.Status == SubtaskStatus.AwaitingReview)
                {
                    hasAwaitingReview = true;
                    state.SetCurrentSubtask(task.Id, subtask.Id);
                }
                else if (subtask.Status != SubtaskStatus.Complete)
                {
                    hasIncomplete = true;
                }
            }
        }

        if (!hasSubtasks)
        {
            return OrchestratorPhase.Breakdown;
        }

        if (hasAwaitingReview)
        {
            return OrchestratorPhase.Verify;
        }

        if (state.Assigned)
        {
            return OrchestratorPhase.Develop;
        }

        if (hasIncomplete)
        {
            return OrchestratorPhase.Assign;
        }

        return OrchestratorPhase.Finalize;
    }

    // Manager breaks ticket into subtasks.
    private async Task RunBreakdownPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manager: Breaking down ticket into subtasks");
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Breaking down ticket");

        state.Clear();

        string userPrompt = $"Break down this ticket into tasks and subtasks:\n\nTicket: {ticketHolder.Ticket.Title}\nDescription: {ticketHolder.Ticket.Description}\n\nCreate a task with create_task, then add subtasks to it with create_subtask.";

        string response = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager breakdown response: {Response}", response);

        if (state.SubtasksCreated)
        {
            _logger.LogInformation("Breakdown complete: {Count} subtasks created", state.SubtaskCount);
        }
        else
        {
            _logger.LogWarning("Breakdown failed - no subtasks created");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to break down ticket");
            state.Blocked = true;
        }
    }

    // Manager assigns the next incomplete subtask to the developer.
    private async Task RunAssignPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
    {
        (string? taskId, string? subtaskId, KanbanSubtaskDto? subtask) = FindNextIncompleteSubtask(ticketHolder.Ticket);

        if (subtask == null)
        {
            _logger.LogInformation("No incomplete subtasks");
            return;
        }

        state.SetCurrentSubtask(taskId!, subtaskId!);
        state.Clear();

        _logger.LogInformation("Manager: Assigning subtask '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Assigning subtask '{subtask.Name}'");

        string userPrompt = $"Assign this subtask to the developer:\n\nSubtask: {subtask.Name}\n\nCall assign_subtask_to_developer with mode and goal.";

        string response = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager assign response: {Response}", response);

        if (state.Assigned)
        {
            _logger.LogInformation("Assignment ready: mode={Mode}", state.AssignedMode);
        }
        else
        {
            _logger.LogWarning("Assignment failed");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to assign subtask");
            state.Blocked = true;
        }
    }

    // Developer implements the assigned subtask.
    private async Task RunDevelopPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
    {
        KanbanSubtaskDto? subtask = FindSubtask(ticketHolder.Ticket, state.CurrentTaskId, state.CurrentSubtaskId);
        if (subtask == null)
        {
            _logger.LogWarning("Current subtask not found");
            return;
        }

        _logger.LogInformation("Developer: Working on '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Developer: Starting work on '{subtask.Name}'");

        TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, state.CurrentTaskId!, state.CurrentSubtaskId!, SubtaskStatus.InProgress);
        ticketHolder.Update(updated);

        state.Clear();

        string userPrompt = $"Complete this subtask:\n\nSubtask: {subtask.Name}\n\nCall mark_subtask_complete when done or blocked.";

        int iterations = 0;

        while (!state.DeveloperComplete && iterations < _maxIterationsPerSubtask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations++;

            string response = await _developerLlmService.RunAsync(_developerImplementationPrompt, userPrompt, toolProviders, LlmRole.Developer, cancellationToken);
            _logger.LogDebug("Developer response ({Iteration}): {Response}", iterations, response.Substring(0, Math.Min(200, response.Length)));

            if (!state.DeveloperComplete)
            {
                userPrompt = "Continue working. When done or blocked, call mark_subtask_complete.";
            }
        }

        if (state.DeveloperComplete)
        {
            updated = await _apiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, state.CurrentTaskId!, state.CurrentSubtaskId!, SubtaskStatus.AwaitingReview);
            ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Developer: Completed work on '{subtask.Name}'");
        }
        else
        {
            _logger.LogWarning("Developer exceeded max iterations ({Max})", _maxIterationsPerSubtask);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Developer: Exceeded max iterations");
            state.Blocked = true;
        }
    }

    // Manager verifies the developer's work and approves or rejects.
    private async Task RunVerifyPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
    {
        KanbanSubtaskDto? subtask = FindSubtask(ticketHolder.Ticket, state.CurrentTaskId, state.CurrentSubtaskId);
        if (subtask == null)
        {
            _logger.LogWarning("Subtask to verify not found");
            return;
        }

        _logger.LogInformation("Manager: Verifying '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Verifying '{subtask.Name}'");

        state.Clear();

        string userPrompt = $"Verify this completed subtask:\n\nSubtask: {subtask.Name}\n\nCall approve_subtask or reject_subtask.";

        string response = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager verify response: {Response}", response);

        if (state.SubtaskApproved == true)
        {
            _logger.LogInformation("Subtask approved");
        }
        else if (state.SubtaskApproved == false)
        {
            _logger.LogInformation("Subtask rejected: {Reason}", state.RejectionReason);
        }
        else
        {
            _logger.LogWarning("Verification failed - no decision made");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Failed to verify '{subtask.Name}'");
            state.Blocked = true;
        }
    }

    // Manager commits, pushes, and marks ticket complete or blocked.
    private async Task RunFinalizePhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manager: Finalizing ticket");
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Running final verification and git operations");

        state.Clear();

        string userPrompt = $"All subtasks complete. Finalize this ticket:\n\nTicket: {ticketHolder.Ticket.Title}\n\n1. Use shell to commit any uncommitted changes with git\n2. Use shell to push to the remote branch with git\n3. Call mark_ticket_complete or mark_ticket_blocked";

        string response = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager finalize response: {Response}", response);

        if (state.TicketComplete == false)
        {
            _logger.LogWarning("Finalization failed: {Reason}", state.BlockedReason);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Finalization failed - {state.BlockedReason}");
            state.Blocked = true;
        }
        else if (state.TicketComplete == null)
        {
            _logger.LogWarning("Finalization failed - no decision made");
            state.Blocked = true;
        }
    }

    // Finds the next subtask that needs work.
    private (string?, string?, KanbanSubtaskDto?) FindNextIncompleteSubtask(TicketDto ticket)
    {
        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                if (subtask.Status == SubtaskStatus.Incomplete ||
                    subtask.Status == SubtaskStatus.Rejected ||
                    subtask.Status == SubtaskStatus.InProgress)
                {
                    return (task.Id, subtask.Id, subtask);
                }
            }
        }

        return (null, null, null);
    }

    // Looks up a subtask by task and subtask ID.
    private KanbanSubtaskDto? FindSubtask(TicketDto ticket, string? taskId, string? subtaskId)
    {
        if (taskId == null || subtaskId == null)
        {
            return null;
        }

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            if (task.Id == taskId)
            {
                foreach (KanbanSubtaskDto subtask in task.Subtasks)
                {
                    if (subtask.Id == subtaskId)
                    {
                        return subtask;
                    }
                }
            }
        }

        return null;
    }
}
