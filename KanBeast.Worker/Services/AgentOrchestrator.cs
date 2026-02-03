using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

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
    private readonly ILlmService _managerLlmService;
    private readonly ILlmService _developerLlmService;
    private readonly IToolExecutor _toolExecutor;
    private readonly string _managerPrompt;
    private readonly string _developerImplementationPrompt;
    private readonly int _maxIterationsPerSubtask;

    private OrchestratorPhase _phase;
    private string? _currentTaskId;
    private string? _currentSubtaskId;

    public AgentOrchestrator(
        ILogger<AgentOrchestrator> logger,
        IKanbanApiClient apiClient,
        ILlmService managerLlmService,
        ILlmService developerLlmService,
        IToolExecutor toolExecutor,
        string managerPrompt,
        string developerImplementationPrompt,
        int maxIterationsPerSubtask)
    {
        _logger = logger;
        _apiClient = apiClient;
        _managerLlmService = managerLlmService;
        _developerLlmService = developerLlmService;
        _toolExecutor = toolExecutor;
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
        TicketTools ticketTools = new TicketTools(_apiClient, ticketHolder);

        _phase = DetermineInitialPhase(ticketHolder.Ticket);
        _logger.LogInformation("Initial phase: {Phase}", _phase);

        int totalIterations = 0;
        int maxTotalIterations = 500;

        while (_phase != OrchestratorPhase.Complete && _phase != OrchestratorPhase.Blocked && totalIterations < maxTotalIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalIterations++;

            _logger.LogDebug("Phase: {Phase} (iteration {Iteration})", _phase, totalIterations);

            switch (_phase)
            {
                case OrchestratorPhase.Breakdown:
                    await RunBreakdownPhaseAsync(ticketHolder, ticketTools, workDir, cancellationToken);
                    break;

                case OrchestratorPhase.Assign:
                    await RunAssignPhaseAsync(ticketHolder, ticketTools, workDir, cancellationToken);
                    break;

                case OrchestratorPhase.Develop:
                    await RunDevelopPhaseAsync(ticketHolder, ticketTools, workDir, cancellationToken);
                    break;

                case OrchestratorPhase.Verify:
                    await RunVerifyPhaseAsync(ticketHolder, ticketTools, workDir, cancellationToken);
                    break;

                case OrchestratorPhase.Finalize:
                    await RunFinalizePhaseAsync(ticketHolder, ticketTools, workDir, cancellationToken);
                    break;
            }
        }

        if (_phase == OrchestratorPhase.Complete)
        {
            _logger.LogInformation("Orchestrator completed successfully");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Completed successfully");
        }
        else if (_phase == OrchestratorPhase.Blocked)
        {
            _logger.LogWarning("Orchestrator blocked - requires human intervention");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Blocked - requires human intervention");
        }
        else
        {
            _logger.LogWarning("Orchestrator exceeded max iterations ({Max})", maxTotalIterations);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Exceeded max iterations ({maxTotalIterations})");
        }
    }

    // Inspects ticket state to determine which phase to start in.
    private OrchestratorPhase DetermineInitialPhase(TicketDto ticket)
    {
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
                    _currentTaskId = task.Id;
                    _currentSubtaskId = subtask.Id;
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
    private async Task RunBreakdownPhaseAsync(TicketHolder ticketHolder, TicketTools ticketTools, string workDir, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manager: Breaking down ticket into subtasks");
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Breaking down ticket");

        ticketTools.ClearResults();

        Kernel kernel = _managerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            ticketTools
        });

        string userPrompt = $"Break down this ticket into subtasks:\n\nTicket: {ticketHolder.Ticket.Title}\nDescription: {ticketHolder.Ticket.Description}\nWorking Directory: {workDir}\n\nCall create_subtasks when ready.";

        await _managerLlmService.ClearContextStatementsAsync(cancellationToken);
        string response = await _managerLlmService.RunAsync(kernel, _managerPrompt, userPrompt, cancellationToken);
        _logger.LogDebug("Manager breakdown response: {Response}", response);

        if (ticketTools.SubtasksCreated)
        {
            _logger.LogInformation("Breakdown complete: {Count} subtasks created", ticketTools.SubtaskCount);
            _phase = OrchestratorPhase.Assign;
        }
        else
        {
            _logger.LogWarning("Breakdown failed - no subtasks created");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to break down ticket");
            _phase = OrchestratorPhase.Blocked;
        }
    }

    // Manager assigns the next incomplete subtask to the developer.
    private async Task RunAssignPhaseAsync(TicketHolder ticketHolder, TicketTools ticketTools, string workDir, CancellationToken cancellationToken)
    {
        (string? taskId, string? subtaskId, KanbanSubtaskDto? subtask) = FindNextIncompleteSubtask(ticketHolder.Ticket);

        if (subtask == null)
        {
            _logger.LogInformation("No incomplete subtasks - moving to finalize");
            _phase = OrchestratorPhase.Finalize;
            return;
        }

        _currentTaskId = taskId;
        _currentSubtaskId = subtaskId;
        ticketTools.SetCurrentSubtask(taskId!, subtaskId!);
        ticketTools.ClearResults();

        _logger.LogInformation("Manager: Assigning subtask '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Assigning subtask '{subtask.Name}'");

        Kernel kernel = _managerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            ticketTools
        });

        string userPrompt = $"Assign this subtask to the developer:\n\nSubtask: {subtask.Name}\nWorking Directory: {workDir}\n\nCall assign_to_developer with mode and goal.";

        await _managerLlmService.ClearContextStatementsAsync(cancellationToken);
        string response = await _managerLlmService.RunAsync(kernel, _managerPrompt, userPrompt, cancellationToken);
        _logger.LogDebug("Manager assign response: {Response}", response);

        if (ticketTools.Assigned)
        {
            _logger.LogInformation("Assignment ready: mode={Mode}", ticketTools.AssignedMode);
            _phase = OrchestratorPhase.Develop;
        }
        else
        {
            _logger.LogWarning("Assignment failed");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to assign subtask");
            _phase = OrchestratorPhase.Blocked;
        }
    }

    // Developer implements the assigned subtask.
    private async Task RunDevelopPhaseAsync(TicketHolder ticketHolder, TicketTools ticketTools, string workDir, CancellationToken cancellationToken)
    {
        KanbanSubtaskDto? subtask = FindSubtask(ticketHolder.Ticket, _currentTaskId, _currentSubtaskId);
        if (subtask == null)
        {
            _logger.LogWarning("Current subtask not found");
            _phase = OrchestratorPhase.Assign;
            return;
        }

        _logger.LogInformation("Developer: Working on '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Developer: Starting work on '{subtask.Name}'");

        TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, _currentTaskId!, _currentSubtaskId!, SubtaskStatus.InProgress);
        ticketHolder.Update(updated);

        ticketTools.ClearResults();

        Kernel kernel = _developerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            ticketTools
        });

        string userPrompt = $"Complete this subtask:\n\nSubtask: {subtask.Name}\nWorking Directory: {workDir}\n\nCall subtask_complete when done or blocked.";

        await _developerLlmService.ClearContextStatementsAsync(cancellationToken);

        int iterations = 0;

        while (!ticketTools.DeveloperComplete && iterations < _maxIterationsPerSubtask)
        {
            cancellationToken.ThrowIfCancellationRequested();
            iterations++;

            string response = await _developerLlmService.RunAsync(kernel, _developerImplementationPrompt, userPrompt, cancellationToken);
            _logger.LogDebug("Developer response ({Iteration}): {Response}", iterations, response.Substring(0, Math.Min(200, response.Length)));

            if (!ticketTools.DeveloperComplete)
            {
                userPrompt = "Continue working. When done or blocked, call subtask_complete.";
            }
        }

        if (ticketTools.DeveloperComplete)
        {
            updated = await _apiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, _currentTaskId!, _currentSubtaskId!, SubtaskStatus.AwaitingReview);
            ticketHolder.Update(updated);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Developer: Completed work on '{subtask.Name}'");
            _phase = OrchestratorPhase.Verify;
        }
        else
        {
            _logger.LogWarning("Developer exceeded max iterations ({Max})", _maxIterationsPerSubtask);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Developer: Exceeded max iterations");
            _phase = OrchestratorPhase.Blocked;
        }
    }

    // Manager verifies the developer's work and approves or rejects.
    private async Task RunVerifyPhaseAsync(TicketHolder ticketHolder, TicketTools ticketTools, string workDir, CancellationToken cancellationToken)
    {
        KanbanSubtaskDto? subtask = FindSubtask(ticketHolder.Ticket, _currentTaskId, _currentSubtaskId);
        if (subtask == null)
        {
            _logger.LogWarning("Subtask to verify not found");
            _phase = OrchestratorPhase.Assign;
            return;
        }

        _logger.LogInformation("Manager: Verifying '{Name}'", subtask.Name);
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Verifying '{subtask.Name}'");

        ticketTools.SetCurrentSubtask(_currentTaskId!, _currentSubtaskId!);
        ticketTools.ClearResults();

        Kernel kernel = _managerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            ticketTools
        });

        string userPrompt = $"Verify this completed subtask:\n\nSubtask: {subtask.Name}\nWorking Directory: {workDir}\n\nCall approve_subtask or reject_subtask.";

        await _managerLlmService.ClearContextStatementsAsync(cancellationToken);
        string response = await _managerLlmService.RunAsync(kernel, _managerPrompt, userPrompt, cancellationToken);
        _logger.LogDebug("Manager verify response: {Response}", response);

        if (ticketTools.SubtaskApproved == true)
        {
            _logger.LogInformation("Subtask approved");
            _phase = OrchestratorPhase.Assign;
        }
        else if (ticketTools.SubtaskApproved == false)
        {
            _logger.LogInformation("Subtask rejected: {Reason}", ticketTools.RejectionReason);
            _phase = OrchestratorPhase.Assign;
        }
        else
        {
            _logger.LogWarning("Verification failed - no decision made");
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Failed to verify '{subtask.Name}'");
            _phase = OrchestratorPhase.Blocked;
        }
    }

    // Manager commits, pushes, and marks ticket complete or blocked.
    private async Task RunFinalizePhaseAsync(TicketHolder ticketHolder, TicketTools ticketTools, string workDir, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manager: Finalizing ticket");
        await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Running final verification and git operations");

        ticketTools.ClearResults();

        Kernel kernel = _managerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            ticketTools
        });

        string userPrompt = $"All subtasks complete. Finalize this ticket:\n\nTicket: {ticketHolder.Ticket.Title}\nWorking Directory: {workDir}\n\n1. Use shell to commit any uncommitted changes with git\n2. Use shell to push to the remote branch with git\n3. Call mark_complete or mark_blocked";

        await _managerLlmService.ClearContextStatementsAsync(cancellationToken);
        string response = await _managerLlmService.RunAsync(kernel, _managerPrompt, userPrompt, cancellationToken);
        _logger.LogDebug("Manager finalize response: {Response}", response);

        if (ticketTools.TicketComplete == true)
        {
            _phase = OrchestratorPhase.Complete;
        }
        else if (ticketTools.TicketComplete == false)
        {
            _logger.LogWarning("Finalization failed: {Reason}", ticketTools.BlockedReason);
            await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager: Finalization failed - {ticketTools.BlockedReason}");
            _phase = OrchestratorPhase.Blocked;
        }
        else
        {
            _logger.LogWarning("Finalization failed - no decision made");
            _phase = OrchestratorPhase.Blocked;
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
