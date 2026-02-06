using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Phases the orchestrator transitions through when processing a ticket.
public enum OrchestratorPhase
{
	Planning,   // Manager breaks down ticket into tasks and subtasks.
	Working,    // Manager coordinates with developer to complete subtasks.
	Done,
	Blocked
}

// Orchestrates the manager and developer LLMs to complete a ticket.
public interface IAgentOrchestrator
{
	Task RunAsync(TicketDto ticket, string workDir, CancellationToken cancellationToken);
}

// Coordinates manager LLM with developer LLM through conversational tools.
public class AgentOrchestrator : IAgentOrchestrator
{
	private readonly ILogger<AgentOrchestrator> _logger;
	private readonly IKanbanApiClient _apiClient;
	private readonly LlmProxy _managerLlm;
	private readonly LlmProxy _developerLlm;
	private readonly string _managerPrompt;
	private readonly string _developerPrompt;

	public AgentOrchestrator(
		ILogger<AgentOrchestrator> logger,
		IKanbanApiClient apiClient,
		LlmProxy managerLlm,
		LlmProxy developerLlm,
		string managerPrompt,
		string developerPrompt)
	{
		_logger = logger;
		_apiClient = apiClient;
		_managerLlm = managerLlm;
		_developerLlm = developerLlm;
		_managerPrompt = managerPrompt;
		_developerPrompt = developerPrompt;
	}

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
			ToolProvidersFactory = () => new List<IToolProvider>
			{
				new ShellTools(workDir),
				new FileTools(workDir),
				new TicketTools(_apiClient, ticketHolder, state)
			}
		};

		TicketTools ticketTools = new TicketTools(_logger, _apiClient, ticketHolder, state, developerConfig);

		List<IToolProvider> managerToolProviders = new List<IToolProvider>
		{
			new ShellTools(workDir),
			new FileTools(workDir),
			ticketTools
		};

		OrchestratorPhase phase = OrchestratorPhase.Planning;
		bool running = true;

		while (running)
		{
			cancellationToken.ThrowIfCancellationRequested();

			decimal currentCost = ticketHolder.Ticket.LlmCost;
			decimal maxCost = ticketHolder.Ticket.MaxCost;

			phase = DeterminePhase(ticketHolder.Ticket, state);
			_logger.LogDebug("Phase: {Phase} (spent ${Spend:F4})", phase, currentCost);

			if (phase == OrchestratorPhase.Done)
			{
				_logger.LogInformation("Orchestrator completed successfully (spent ${Spend:F4})", currentCost);
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Completed successfully (spent ${currentCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Done");
				running = false;
			}
			else if (phase == OrchestratorPhase.Blocked)
			{
				_logger.LogWarning("Orchestrator blocked - requires human intervention (spent ${Spend:F4})", currentCost);
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Blocked - requires human intervention (spent ${currentCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed");
				running = false;
			}
			else if (maxCost > 0 && currentCost >= maxCost)
			{
				_logger.LogWarning("Orchestrator exceeded max cost (${Spend:F4} >= ${Max:F4})", currentCost, maxCost);
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Exceeded max cost (${currentCost:F4} >= ${maxCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed");
				running = false;
			}
			else if (phase == OrchestratorPhase.Planning)
			{
				await RunPlanningAsync(ticketHolder, state, managerToolProviders, cancellationToken);
			}
			else if (phase == OrchestratorPhase.Working)
			{
				await RunWorkingAsync(ticketHolder, state, managerToolProviders, workDir, cancellationToken);
			}
		}
	}

	private OrchestratorPhase DeterminePhase(TicketDto ticket, TaskState state)
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
		bool allComplete = true;

		foreach (KanbanTaskDto task in ticket.Tasks)
		{
			foreach (KanbanSubtaskDto subtask in task.Subtasks)
			{
				hasSubtasks = true;
				if (subtask.Status != SubtaskStatus.Complete)
				{
					allComplete = false;
				}
			}
		}

		if (!hasSubtasks)
		{
			return OrchestratorPhase.Planning;
		}

		if (allComplete)
		{
			return OrchestratorPhase.Done;
		}

		return OrchestratorPhase.Working;
	}

	private async Task RunPlanningAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Manager: Planning ticket breakdown");
		await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Planning ticket breakdown");

		state.Clear();

		string userPrompt = $"""
			Break down this ticket into tasks and subtasks:

			Ticket: {ticketHolder.Ticket.Title}
			Description: {ticketHolder.Ticket.Description}

			There must be one or more tasks, and each task must have one or more subtasks.
			Use create_task to create tasks, then create_subtask to add subtasks with clear acceptance criteria.
			""";

		LlmResult result = await _managerLlm.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.ManagerPlanning, cancellationToken);
		_logger.LogDebug("Manager planning response: {Response}", result.Content);

		if (result.AccumulatedCost > 0)
		{
			TicketDto? updated = await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
			if (updated != null)
			{
				ticketHolder.Update(updated);
			}
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

	private async Task RunWorkingAsync(TicketHolder ticketHolder, TaskState state, List<IToolProvider> toolProviders, string workDir, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Manager: Working on ticket");

		string ticketSummary = FormatTicketStatus(ticketHolder.Ticket);

		string userPrompt = $"""
			Continue working on this ticket. Review the current status and take the next action.

			{ticketSummary}

			Repository: {workDir}

			Workflow: Complete subtasks in the exact order they appear. For each incomplete subtask, assign it to the developer, review their response, provide feedback or mark complete, then move to the next subtask.
			Do not skip ahead or work on subtasks out of order.
			When all subtasks are complete, set the ticket status to done.
			""";

		LlmResult result = await _managerLlm.RunAsync(_managerPrompt, userPrompt, toolProviders, LlmRole.ManagerImplementing, cancellationToken);
		_logger.LogDebug("Manager working response: {Response}", result.Content);

		if (result.AccumulatedCost > 0)
		{
			TicketDto? updated = await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
			if (updated != null)
			{
				ticketHolder.Update(updated);
			}
		}

		if (!result.Success)
		{
			_logger.LogError("Manager LLM failed during work: {Error}", result.ErrorMessage);
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {result.ErrorMessage}");
			state.Blocked = true;
		}
	}

	private static string FormatTicketStatus(TicketDto ticket)
	{
		System.Text.StringBuilder sb = new System.Text.StringBuilder();
		sb.AppendLine($"Ticket: {ticket.Title}");
		sb.AppendLine($"Description: {ticket.Description}");
		sb.AppendLine();
		sb.AppendLine("Current Status:");

		foreach (KanbanTaskDto task in ticket.Tasks)
		{
			sb.AppendLine($"  Task: {task.Name}");
			foreach (KanbanSubtaskDto subtask in task.Subtasks)
			{
				sb.AppendLine($"    [{subtask.Status}] {subtask.Name}");
			}
		}

		return sb.ToString();
	}
}
