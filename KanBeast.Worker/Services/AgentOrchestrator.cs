using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Phases the orchestrator transitions through when processing a ticket.
public enum OrchestratorPhase
{
    Breakdown,
    Assign,
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

// Coordinates LLM-driven phases: breakdown, assign, verify, finalize.
public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly IKanbanApiClient _apiClient;
    private readonly LlmProxy _managerLlmService;
    private readonly LlmProxy _developerLlmService;
    private readonly string _managerPrompt;
    private readonly string _developerPrompt;
    private readonly int _maxIterationsPerSubtask;

    private OrchestratorPhase _phase;

    public AgentOrchestrator(
        ILogger<AgentOrchestrator> logger,
        IKanbanApiClient apiClient,
        LlmProxy managerLlmService,
        LlmProxy developerLlmService,
        string managerPrompt,
        string developerPrompt,
        int maxIterationsPerSubtask)
    {
        _logger = logger;
        _apiClient = apiClient;
        _managerLlmService = managerLlmService;
        _developerLlmService = developerLlmService;
        _managerPrompt = managerPrompt;
        _developerPrompt = developerPrompt;
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

        DeveloperConfig developerConfig = new DeveloperConfig
        {
            LlmProxy = _developerLlmService,
            Prompt = _developerPrompt,
            WorkDir = workDir,
            MaxIterationsPerSubtask = _maxIterationsPerSubtask,
            ToolProvidersFactory = () => new List<IToolProvider>
            {
                new ShellTools(),
                new FileTools(),
                new TicketTools(_apiClient, ticketHolder, state)
            }
        };

        TicketTools ticketTools = new TicketTools(_logger, _apiClient, ticketHolder, state, developerConfig);

        List<IToolProvider> toolProviders = new List<IToolProvider>
        {
            new ShellTools(),
            new FileTools(),
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
                    await RunAssignPhaseAsync(ticketHolder, state, toolProviders, workDir, cancellationToken);
                    break;

                case OrchestratorPhase.Verify:
                    await RunVerifyPhaseAsync(ticketHolder, state, toolProviders, workDir, cancellationToken);
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

        LlmResult result = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager breakdown response: {Response}", result.Content);

        if (result.AccumulatedCost > 0)
        {
            await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
        }

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
    private async Task RunAssignPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, string workDir, CancellationToken cancellationToken)
    {
        (KanbanTaskDto? task, KanbanSubtaskDto? subtask) = FindNextIncompleteSubtaskWithTask(ticketHolder.Ticket);

        if (task == null || subtask == null)
        {
            _logger.LogInformation("No incomplete subtasks");
            return;
        }

        state.SetCurrentSubtask(task.Id, subtask.Id);
        state.Clear();

        _logger.LogInformation("Manager: Assigning subtask '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Assigning subtask '{subtask.Name}'");

        string acceptanceCriteria = subtask.AcceptanceCriteria.Count > 0
            ? $"""

              Acceptance Criteria:
              {string.Join("\n", subtask.AcceptanceCriteria.Select(c => $"  - {c}"))}
              """
            : string.Empty;

        string constraints = subtask.Constraints.Count > 0
            ? $"""

              Constraints:
              {string.Join("\n", subtask.Constraints.Select(c => $"  - {c}"))}
              """
            : string.Empty;

        string rejectionNotes = !string.IsNullOrEmpty(subtask.LastRejectionNotes)
            ? $"""

              Previous rejection feedback (must be addressed): {subtask.LastRejectionNotes}
              """
            : string.Empty;

        string userPrompt = $"""
            Assign this subtask to the developer by calling assign_subtask_to_developer with mode and a clear goal.

            Task: {task.Name}
            Task Description: {task.Description}

            Subtask: {subtask.Name}
            Subtask Description: {subtask.Description}{acceptanceCriteria}{constraints}{rejectionNotes}

            Repository: {workDir}
            """;

        LlmResult result = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager assign response: {Response}", result.Content);

        if (result.AccumulatedCost > 0)
        {
            await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
        }

        if (state.Assigned)
        {
            _logger.LogInformation("Assignment complete");
        }
        else
        {
            _logger.LogWarning("Assignment failed");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to assign subtask");
            state.Blocked = true;
        }
    }

    // Manager verifies the developer's work and approves or rejects.
    private async Task RunVerifyPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, string workDir, CancellationToken cancellationToken)
    {
        (KanbanTaskDto? task, KanbanSubtaskDto? subtask) = FindTaskAndSubtask(ticketHolder.Ticket, state.CurrentTaskId, state.CurrentSubtaskId);
        if (task == null || subtask == null)
        {
            _logger.LogWarning("Subtask to verify not found");
            return;
        }

        _logger.LogInformation("Manager: Verifying '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Verifying '{subtask.Name}'");

        string developerSummary = state.DeveloperMessage;
        state.Clear();

        string acceptanceCriteria = subtask.AcceptanceCriteria.Count > 0
            ? $"""

              Acceptance Criteria to verify:
              {string.Join("\n", subtask.AcceptanceCriteria.Select(c => $"  - {c}"))}
              """
            : string.Empty;

        string developerSummarySection = !string.IsNullOrEmpty(developerSummary)
            ? $"""

              Developer's completion summary: {developerSummary}
              """
            : string.Empty;

        string userPrompt = $"""
            Verify this completed subtask. Build the project, run tests, and inspect the changed files to confirm the work meets acceptance criteria.

            Task: {task.Name}
            Task Description: {task.Description}

            Subtask: {subtask.Name}
            Subtask Description: {subtask.Description}{acceptanceCriteria}{developerSummarySection}

            Repository: {workDir}

            Verification steps:
            1. Ensure the project builds successfully
            2. Run and ensure all tests pass
            3. Inspect the files the developer claims to have modified
            4. Verify each acceptance criterion is met

            Call approve_subtask if all criteria are met and tests pass.
            Call reject_subtask with specific feedback if any criteria are not met or tests fail.
            """;

        LlmResult result = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager verify response: {Response}", result.Content);

        if (result.AccumulatedCost > 0)
        {
            await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
        }

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

        string userPrompt = $"""
            All subtasks complete. Finalize this ticket:

            Ticket: {ticketHolder.Ticket.Title}

            1. Use shell to commit any uncommitted changes with git
            2. Use shell to push to the remote branch with git
            3. Call mark_ticket_complete or mark_ticket_blocked
            """;

        LlmResult result = await _managerLlmService.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager finalize response: {Response}", result.Content);

        if (result.AccumulatedCost > 0)
        {
            await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
        }

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

    // Finds the next subtask that needs work, along with its parent task.
    private (KanbanTaskDto?, KanbanSubtaskDto?) FindNextIncompleteSubtaskWithTask(TicketDto ticket)
    {
        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                if (subtask.Status == SubtaskStatus.Incomplete ||
                    subtask.Status == SubtaskStatus.Rejected ||
                    subtask.Status == SubtaskStatus.InProgress)
                {
                    return (task, subtask);
                }
            }
        }

        return (null, null);
    }

    // Looks up a task and subtask by their IDs.
    private (KanbanTaskDto?, KanbanSubtaskDto?) FindTaskAndSubtask(TicketDto ticket, string? taskId, string? subtaskId)
    {
        if (taskId == null || subtaskId == null)
        {
            return (null, null);
        }

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            if (task.Id == taskId)
            {
                foreach (KanbanSubtaskDto subtask in task.Subtasks)
                {
                    if (subtask.Id == subtaskId)
                    {
                        return (task, subtask);
                    }
                }
            }
        }

        return (null, null);
    }
}
