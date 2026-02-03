using System.Text;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services;

// Orchestrates the manager-developer agent loop
public interface IAgentOrchestrator
{
    Task RunAsync(TicketDto ticket, string workDir, CancellationToken cancellationToken);
}

public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IKanbanApiClient _apiClient;
    private readonly ILlmService _managerLlmService;
    private readonly ILlmService _developerLlmService;
    private readonly IToolExecutor _toolExecutor;
    private readonly IGitService _gitService;
    private readonly string _managerPrompt;
    private readonly string _developerImplementationPrompt;
    private readonly string _developerTestingPrompt;
    private readonly string _developerWriteTestsPrompt;
    private readonly OrchestratorState _state;
    private readonly int _maxIterationsPerSubtask;

    public AgentOrchestrator(
        IKanbanApiClient apiClient,
        ILlmService managerLlmService,
        ILlmService developerLlmService,
        IToolExecutor toolExecutor,
        IGitService gitService,
        string managerPrompt,
        string developerImplementationPrompt,
        string developerTestingPrompt,
        string developerWriteTestsPrompt,
        int maxIterationsPerSubtask)
    {
        _apiClient = apiClient;
        _managerLlmService = managerLlmService;
        _developerLlmService = developerLlmService;
        _toolExecutor = toolExecutor;
        _gitService = gitService;
        _managerPrompt = managerPrompt;
        _developerImplementationPrompt = developerImplementationPrompt;
        _developerTestingPrompt = developerTestingPrompt;
        _developerWriteTestsPrompt = developerWriteTestsPrompt;
        _state = new OrchestratorState();
        _maxIterationsPerSubtask = maxIterationsPerSubtask;
    }

    public async Task RunAsync(TicketDto ticket, string workDir, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_managerPrompt))
        {
            Console.WriteLine("Error: Manager prompt is empty or null");
            throw new InvalidOperationException("Manager prompt is required but was empty or null.");
        }

        if (string.IsNullOrWhiteSpace(_developerImplementationPrompt))
        {
            Console.WriteLine("Error: Developer implementation prompt is empty or null");
            throw new InvalidOperationException("Developer implementation prompt is required but was empty or null.");
        }

        ManagerTools managerTools = new ManagerTools(_apiClient, _state, ticket.Id);
        DeveloperTaskTools developerTools = new DeveloperTaskTools(_apiClient, _state, ticket.Id);

        Kernel managerKernel = _managerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            new KanbanTools(_apiClient),
            managerTools
        });

        Kernel developerKernel = _developerLlmService.CreateKernel(new object[]
        {
            new ShellTools(_toolExecutor),
            new FileTools(_toolExecutor),
            developerTools
        });

        bool isComplete = false;
        int totalIterations = 0;
        int maxTotalIterations = 1000;

        while (!isComplete && totalIterations < maxTotalIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalIterations++;

            if (_state.CurrentAgent == AgentRole.Manager)
            {
                isComplete = await RunManagerIterationAsync(ticket, workDir, managerKernel, managerTools, cancellationToken);
            }
            else
            {
                await RunDeveloperIterationAsync(ticket, workDir, developerKernel, developerTools, cancellationToken);
            }

            // Refresh ticket state
            TicketDto? refreshed = await _apiClient.GetTicketAsync(ticket.Id);

            if (refreshed == null)
            {
                string errorMessage = $"Failed to refresh ticket {ticket.Id} - ticket may have been deleted";
                Console.WriteLine($"Error: {errorMessage}");
                await _apiClient.AddActivityLogAsync(ticket.Id, $"Orchestrator: {errorMessage}");
                throw new InvalidOperationException(errorMessage);
            }

            ticket = refreshed;
        }

        if (totalIterations >= maxTotalIterations)
        {
            string errorMessage = $"Maximum iterations ({maxTotalIterations}) reached without completion";
            Console.WriteLine($"Error: {errorMessage}");
            await _apiClient.AddActivityLogAsync(ticket.Id, $"Orchestrator: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
    }

    private async Task<bool> RunManagerIterationAsync(
        TicketDto ticket,
        string workDir,
        Kernel kernel,
        ManagerTools managerTools,
        CancellationToken cancellationToken)
    {
        managerTools.ClearLastToolResult();

        string context = BuildManagerContext(ticket, workDir);
        await _managerLlmService.ClearContextStatementsAsync(cancellationToken);
        await _managerLlmService.AddContextStatementAsync(context, cancellationToken);

        string userPrompt = DetermineManagerUserPrompt(ticket);
        Console.WriteLine($"Manager iteration - {userPrompt}");

        string response = await _managerLlmService.RunAsync(kernel, _managerPrompt, userPrompt, cancellationToken);
        Console.WriteLine($"Manager response: {response}");

        AgentToolResult? toolResult = managerTools.GetLastToolResult();

        if (toolResult != null)
        {
            if (toolResult.IsTerminal)
            {
                return true;
            }

            if (toolResult.ShouldTransition && toolResult.NextAgent == AgentRole.Developer)
            {
                _state.CurrentAgent = AgentRole.Developer;
                _state.CurrentDeveloperMode = toolResult.NextDeveloperMode;
            }
        }

        return false;
    }

    private async Task RunDeveloperIterationAsync(
        TicketDto ticket,
        string workDir,
        Kernel kernel,
        DeveloperTaskTools developerTools,
        CancellationToken cancellationToken)
    {
        developerTools.ClearLastToolResult();

        string developerPrompt = GetDeveloperPrompt(_state.CurrentDeveloperMode);
        string context = BuildDeveloperContext(workDir);

        await _developerLlmService.ClearContextStatementsAsync(cancellationToken);
        await _developerLlmService.AddContextStatementAsync(context, cancellationToken);

        string userPrompt = "Complete the assigned work using available tools. When finished, call subtask_complete.";
        Console.WriteLine($"Developer iteration ({_state.CurrentDeveloperMode}) - working on subtask");

        int developerIterations = 0;
        int maxDeveloperIterations = 50;
        AgentToolResult? toolResult = null;

        while (developerIterations < maxDeveloperIterations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            developerIterations++;

            string response = await _developerLlmService.RunAsync(kernel, developerPrompt, userPrompt, cancellationToken);
            Console.WriteLine($"Developer response: {response}");

            toolResult = developerTools.GetLastToolResult();

            if (toolResult != null && toolResult.ShouldTransition)
            {
                break;
            }

            // If no transition tool called, continue with follow-up
            userPrompt = "Continue working. If you are done, call subtask_complete.";
        }

        if (toolResult != null && toolResult.ShouldTransition && toolResult.NextAgent == AgentRole.Manager)
        {
            _state.CurrentAgent = AgentRole.Manager;
        }
        else if (developerIterations >= maxDeveloperIterations)
        {
            string errorMessage = $"Developer agent exceeded maximum iterations ({maxDeveloperIterations}) without calling subtask_complete";
            Console.WriteLine($"Error: {errorMessage}");
            await _apiClient.AddActivityLogAsync(ticket.Id, $"Developer: {errorMessage}");
            throw new InvalidOperationException(errorMessage);
        }
    }

    private string BuildManagerContext(TicketDto ticket, string workDir)
    {
        StringBuilder builder = new StringBuilder();

        builder.AppendLine($"Ticket ID: {ticket.Id}");
        builder.AppendLine($"Title: {ticket.Title}");
        builder.AppendLine($"Description: {ticket.Description}");
        builder.AppendLine($"Status: {ticket.Status}");
        builder.AppendLine($"Working Directory: {workDir}");
        builder.AppendLine();

        if (ticket.Tasks.Count > 0)
        {
            builder.AppendLine("## Tasks and Subtasks");

            foreach (KanbanTaskDto task in ticket.Tasks)
            {
                builder.AppendLine($"### Task: {task.Name}");

                foreach (KanbanSubtaskDto subtask in task.Subtasks)
                {
                    string statusIcon = subtask.Status switch
                    {
                        SubtaskStatus.Complete => "[x]",
                        SubtaskStatus.InProgress => "[>]",
                        SubtaskStatus.AwaitingReview => "[?]",
                        SubtaskStatus.Blocked => "[!]",
                        SubtaskStatus.Rejected => "[-]",
                        _ => "[ ]"
                    };

                    builder.AppendLine($"- {statusIcon} {subtask.Name} (ID: {subtask.Id}, Status: {subtask.Status})");

                    if (subtask.RejectionCount > 0)
                    {
                        builder.AppendLine($"  Rejection count: {subtask.RejectionCount}");
                    }

                    if (!string.IsNullOrEmpty(subtask.LastRejectionNotes))
                    {
                        builder.AppendLine($"  Last rejection: {subtask.LastRejectionNotes}");
                    }
                }
            }

            builder.AppendLine();
        }

        // Find current subtask for context
        FindCurrentSubtask(ticket);

        if (_state.LastDeveloperResult != null)
        {
            builder.AppendLine("## Developer Completion Report");
            builder.AppendLine($"Status: {_state.LastDeveloperResult.Status}");
            builder.AppendLine($"Build: {_state.LastDeveloperResult.BuildStatus}");
            builder.AppendLine($"Message: {_state.LastDeveloperResult.Message}");

            if (_state.LastDeveloperResult.FilesChanged.Count > 0)
            {
                builder.AppendLine("Files Changed:");
                foreach (FileChangeInfo file in _state.LastDeveloperResult.FilesChanged)
                {
                    builder.AppendLine($"- {file.Path}: {file.Summary}");
                }
            }

            if (_state.LastDeveloperResult.TestResults != null)
            {
                TestResultInfo tests = _state.LastDeveloperResult.TestResults;
                builder.AppendLine($"Tests: {tests.Passed}/{tests.Total} passed, {tests.Failed} failed, {tests.Skipped} skipped");
            }

            if (_state.LastDeveloperResult.BlockerDetails != null)
            {
                BlockerInfo blocker = _state.LastDeveloperResult.BlockerDetails;
                builder.AppendLine($"Blocker: {blocker.Issue}");
                builder.AppendLine($"Needed: {blocker.Needed}");
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    private string BuildDeveloperContext(string workDir)
    {
        StringBuilder builder = new StringBuilder();

        builder.AppendLine($"Working Directory: {workDir}");
        builder.AppendLine();

        if (_state.LastManagerAssignment != null)
        {
            AssignToDeveloperParams assignment = _state.LastManagerAssignment;

            builder.AppendLine("## Current Assignment");
            builder.AppendLine($"Mode: {assignment.Mode}");
            builder.AppendLine($"Goal: {assignment.Goal}");
            builder.AppendLine();

            if (assignment.FilesToInspect.Count > 0)
            {
                builder.AppendLine("### Files to Inspect");
                foreach (string file in assignment.FilesToInspect)
                {
                    builder.AppendLine($"- {file}");
                }
                builder.AppendLine();
            }

            if (assignment.FilesToModify.Count > 0)
            {
                builder.AppendLine("### Files to Modify");
                foreach (string file in assignment.FilesToModify)
                {
                    builder.AppendLine($"- {file}");
                }
                builder.AppendLine();
            }

            if (assignment.AcceptanceCriteria.Count > 0)
            {
                builder.AppendLine("### Acceptance Criteria");
                foreach (string criterion in assignment.AcceptanceCriteria)
                {
                    builder.AppendLine($"- [ ] {criterion}");
                }
                builder.AppendLine();
            }

            if (assignment.Constraints.Count > 0)
            {
                builder.AppendLine("### Constraints");
                foreach (string constraint in assignment.Constraints)
                {
                    builder.AppendLine($"- {constraint}");
                }
                builder.AppendLine();
            }

            if (!string.IsNullOrEmpty(assignment.PriorContext))
            {
                builder.AppendLine("### Prior Context");
                builder.AppendLine(assignment.PriorContext);
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private void FindCurrentSubtask(TicketDto ticket)
    {
        // Find the first incomplete or in-progress subtask
        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                if (subtask.Status == SubtaskStatus.InProgress ||
                    subtask.Status == SubtaskStatus.AwaitingReview ||
                    subtask.Status == SubtaskStatus.Incomplete ||
                    subtask.Status == SubtaskStatus.Rejected)
                {
                    _state.CurrentTaskId = task.Id;
                    _state.CurrentSubtaskId = subtask.Id;
                    return;
                }
            }
        }

        _state.CurrentTaskId = null;
        _state.CurrentSubtaskId = null;
    }

    private string DetermineManagerUserPrompt(TicketDto ticket)
    {
        // Check if developer just returned
        if (_state.LastDeveloperResult != null)
        {
            if (_state.LastDeveloperResult.Status == SubtaskCompleteStatus.Blocked)
            {
                return "Developer reported blocked. Determine how to proceed.";
            }

            return "Developer work complete. Verify the work and determine next steps.";
        }

        // Check ticket state
        bool hasSubtasks = false;
        bool hasIncomplete = false;
        bool allComplete = true;

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                hasSubtasks = true;

                if (subtask.Status != SubtaskStatus.Complete)
                {
                    allComplete = false;
                    hasIncomplete = true;
                }
            }
        }

        if (!hasSubtasks)
        {
            return "Break down the ticket into subtasks.";
        }

        if (allComplete)
        {
            return "All subtasks complete. Run tests and finalize.";
        }

        if (hasIncomplete)
        {
            return "Assign the next incomplete subtask to the developer.";
        }

        return "Determine the current state and next action.";
    }

    private string GetDeveloperPrompt(string? mode)
    {
        string prompt = mode switch
        {
            DeveloperMode.Testing => _developerTestingPrompt,
            DeveloperMode.WriteTests => _developerWriteTestsPrompt,
            _ => _developerImplementationPrompt
        };

        return prompt;
    }
}
