using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Phases the orchestrator transitions through when processing a ticket.
public enum OrchestratorPhase
{
	Planning,   // Manager converts a ticket into tasks and subtasks. Always includes a final task for git completion.
	Implement,  // Manager assigns a subtask to the developer, who implements it and tells the manager what they did.
	Verify,     // Manager verifies the subtask is complete by checking out the code, running tests, and inspecting changes. Manager approves or rejects with feedback.
	Done,
	Blocked
}

// Orchestrates the manager and developer LLMs to complete a ticket.
public interface IAgentOrchestrator
{
    Task RunAsync(TicketDto ticket, string workDir, CancellationToken cancellationToken);
}

// Coordinates LLM-driven phases: planning, implement, verify.
public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly ILogger<AgentOrchestrator> _logger;
    private readonly IKanbanApiClient _apiClient;
    private readonly LlmProxy _managerLlm;
    private readonly LlmProxy _developerLlm;
    private readonly string _managerPrompt;
    private readonly string _developerPrompt;
    private readonly int _maxIterationsPerSubtask;
    private readonly int _stuckPromptingEvery;

    private OrchestratorPhase _phase;

    public AgentOrchestrator(
        ILogger<AgentOrchestrator> logger,
        IKanbanApiClient apiClient,
        LlmProxy managerLlm,
        LlmProxy developerLlm,
        string managerPrompt,
        string developerPrompt,
        int maxIterationsPerSubtask, int stuckPromptingEvery)
    {
        _logger = logger;
        _apiClient = apiClient;
        _managerLlm = managerLlm;
        _developerLlm = developerLlm;
        _managerPrompt = managerPrompt;
        _developerPrompt = developerPrompt;
        _maxIterationsPerSubtask = maxIterationsPerSubtask;
        _stuckPromptingEvery = stuckPromptingEvery;
        _phase = OrchestratorPhase.Planning;
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
            LlmProxy = _developerLlm,
            Prompt = _developerPrompt,
            WorkDir = workDir,
            MaxIterationsPerSubtask = _maxIterationsPerSubtask,
			StuckPromptingEvery = _stuckPromptingEvery,
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

			if (_phase == OrchestratorPhase.Done)
			{
				_logger.LogInformation("Orchestrator completed successfully");
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Completed successfully");
				await _apiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Done");
				break;
			}

			if (_phase == OrchestratorPhase.Blocked)
			{
				_logger.LogWarning("Orchestrator blocked - requires human intervention");
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Blocked - requires human intervention");
				await _apiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed");
				break;
			}

			if (totalIterations >= maxTotalIterations)
			{
				_logger.LogWarning("Orchestrator exceeded max iterations ({Max})", maxTotalIterations);
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Exceeded max iterations ({maxTotalIterations})");
				await _apiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed");
				break;
			}

            switch (_phase)
            {
                case OrchestratorPhase.Planning:
                    await RunPlanningPhaseAsync(ticketHolder, state, toolProviders, cancellationToken);
                    break;

                case OrchestratorPhase.Implement:
                    await RunImplementPhaseAsync(ticketHolder, state, toolProviders, workDir, cancellationToken);
                    break;

                case OrchestratorPhase.Verify:
                    await RunVerifyPhaseAsync(ticketHolder, state, toolProviders, workDir, cancellationToken);
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
            return OrchestratorPhase.Done;
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
            return OrchestratorPhase.Planning;
        }

        if (hasAwaitingReview)
        {
            return OrchestratorPhase.Verify;
        }

        if (hasIncomplete)
        {
            return OrchestratorPhase.Implement;
        }

        return OrchestratorPhase.Done;
    }

    // Manager breaks ticket into subtasks.
    private async Task RunPlanningPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manager: Planning ticket breakdown into subtasks");
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Planning ticket breakdown");

        state.Clear();

        string userPrompt = $"""
            Break down this ticket into tasks and subtasks:

            Ticket: {ticketHolder.Ticket.Title}
            Description: {ticketHolder.Ticket.Description}

            Create a task with create_task, then add subtasks to it with create_subtask.
            """;

        LlmResult result = await _managerLlm.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
        _logger.LogDebug("Manager planning response: {Response}", result.Content);

        if (result.AccumulatedCost > 0)
        {
            await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
        }

        if (result.Success && state.SubtasksCreated)
        {
            _logger.LogInformation("Planning complete: {Count} subtasks created", state.SubtaskCount);
        }
        else if (!result.Success)
        {
            _logger.LogError("Manager LLM failed during planning: {Error}", result.ErrorMessage);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {result.ErrorMessage}");
            state.Blocked = true;
        }
        else
        {
            _logger.LogWarning("Planning failed - no subtasks created");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to plan ticket");
            state.Blocked = true;
        }
    }

    // Manager assigns the next incomplete subtask to the developer.
    private async Task RunImplementPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, string workDir, CancellationToken cancellationToken)
    {
        (KanbanTaskDto? task, KanbanSubtaskDto? subtask) = FindNextIncompleteSubtaskWithTask(ticketHolder.Ticket);

        if (task == null || subtask == null)
        {
            _logger.LogInformation("No incomplete subtasks");
        }
        else
        {
            state.SetCurrentSubtask(task.Id, subtask.Id);

            bool isRetry = subtask.Status == SubtaskStatus.Rejected;
            string logMessage = isRetry
                ? $"Manager: Re-assigning rejected subtask '{subtask.Name}'"
                : $"Manager: Assigning subtask '{subtask.Name}'";

            _logger.LogInformation("{Message}", logMessage);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, logMessage);

            string rejectionNotes = !string.IsNullOrEmpty(state.RejectionReason)
                ? $"""

                  Previous rejection feedback (must be addressed): {state.RejectionReason}
                  """
                : string.Empty;

            state.Clear();

            string userPrompt = $"""
                Assign this subtask to the developer by calling assign_subtask_to_developer with mode and a clear goal.

                Task: {task.Name}
                Task Description: {task.Description}

                Subtask: {subtask.Name}
                Subtask Description: {subtask.Description}{rejectionNotes}

                Repository: {workDir}
                """;

            LlmResult result = await _managerLlm.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
            _logger.LogDebug("Manager assign response: {Response}", result.Content);

            if (result.AccumulatedCost > 0)
            {
                await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
            }

            if (result.Success && state.Assigned)
            {
                _logger.LogInformation("Assignment complete");
            }
            else if (!result.Success)
            {
                _logger.LogError("Manager LLM failed during assignment: {Error}", result.ErrorMessage);
                await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {result.ErrorMessage}");
                state.Blocked = true;
            }
            else
            {
                _logger.LogWarning("Assignment failed");
                await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to assign subtask");
                state.Blocked = true;
            }
        }
    }

    // Manager verifies the developer's work and approves or rejects.
    private async Task RunVerifyPhaseAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, string workDir, CancellationToken cancellationToken)
    {
        (KanbanTaskDto? task, KanbanSubtaskDto? subtask) = FindTaskAndSubtask(ticketHolder.Ticket, state.CurrentTaskId, state.CurrentSubtaskId);

        if (task == null || subtask == null)
        {
            _logger.LogWarning("Subtask to verify not found");
        }
        else
        {
            _logger.LogInformation("Manager: Verifying '{Name}'", subtask.Name);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Verifying '{subtask.Name}'");

            string developerSummary = state.DeveloperMessage;
            state.Clear();

            string developerSummarySection = !string.IsNullOrEmpty(developerSummary)
                ? $"""

                  Developer's completion summary: {developerSummary}
                  """
                : string.Empty;

            string userPrompt = $"""
                Verify this completed subtask. Build the project, run tests, and inspect the changed files to confirm the work meets the acceptance criteria in the subtask description.

                Task: {task.Name}
                Task Description: {task.Description}

                Subtask: {subtask.Name}
                Subtask Description: {subtask.Description}{developerSummarySection}

                Repository: {workDir}

                Verification steps:
                1. Ensure the project builds successfully
                2. Run and ensure all tests pass
                3. Inspect the files the developer claims to have modified
                4. Verify each acceptance criterion is met

                Call approve_subtask if all criteria are met and tests pass.
                Call reject_subtask with specific feedback if any criteria are not met or tests fail.
                """;

            LlmResult result = await _managerLlm.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.Manager, cancellationToken);
            _logger.LogDebug("Manager verify response: {Response}", result.Content);

            if (result.AccumulatedCost > 0)
            {
                await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
            }

            if (!result.Success)
            {
                _logger.LogError("Manager LLM failed during verification: {Error}", result.ErrorMessage);
                await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {result.ErrorMessage}");
                state.Blocked = true;
            }
            else if (state.SubtaskApproved == true)
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
