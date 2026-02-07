using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Phases the orchestrator transitions through when processing a ticket.
public enum OrchestratorPhase
{
	Planning,
	Working,
	Done
}

// Orchestrates the manager and developer LLMs to complete a ticket.
public interface IAgentOrchestrator
{
	Task StartAgents(TicketDto ticket, string workDir, CancellationToken cancellationToken);
}

// Coordinates manager LLM with developer LLM.
public class AgentOrchestrator : IAgentOrchestrator
{
	private const int CircuitBreakerThreshold = 15;

	private readonly ILogger<AgentOrchestrator> _logger;
	private readonly IKanbanApiClient _apiClient;
	private readonly LlmProxy _managerLlm;
	private readonly LlmProxy _developerLlm;
	private readonly string _managerPlanningPrompt;
	private readonly string _managerImplementingPrompt;
	private readonly string _developerPrompt;

	private TicketHolder? _ticketHolder;
	private string? _workDir;
	private List<Tool>? _managerPlanningTools;
	private List<Tool>? _managerImplementingTools;
	private List<Tool>? _developerTools;
	private LlmConversation? _managerConversation;
	private LlmConversation? _developerConversation;
	private string? _currentTaskId;
	private string? _currentSubtaskId;

	public AgentOrchestrator(
		ILogger<AgentOrchestrator> logger,
		IKanbanApiClient apiClient,
		LlmProxy managerLlm,
		LlmProxy developerLlm,
		string managerPlanningPrompt,
		string managerImplementingPrompt,
		string developerPrompt)
	{
		_logger = logger;
		_apiClient = apiClient;
		_managerLlm = managerLlm;
		_developerLlm = developerLlm;
		_managerPlanningPrompt = managerPlanningPrompt;
		_managerImplementingPrompt = managerImplementingPrompt;
		_developerPrompt = developerPrompt;
	}

	public async Task StartAgents(TicketDto ticket, string workDir, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Orchestrator starting for ticket: {Title}", ticket.Title);
		await _apiClient.AddActivityLogAsync(ticket.Id, "Orchestrator: Starting work");

		_ticketHolder = new TicketHolder(ticket);
		_workDir = workDir;

		TicketTools ticketTools = new TicketTools(_apiClient, _ticketHolder);
		ShellTools shellTools = new ShellTools(workDir);
		FileTools fileTools = new FileTools(workDir);

		_managerPlanningTools = BuildManagerPlanningTools(shellTools, fileTools, ticketTools);
		_managerImplementingTools = BuildManagerImplementingTools(shellTools, fileTools, ticketTools);
		_developerTools = BuildDeveloperTools(shellTools, fileTools);

		bool running = true;

		while (running)
		{
			cancellationToken.ThrowIfCancellationRequested();

			decimal currentCost = _ticketHolder.Ticket.LlmCost;
			decimal maxCost = _ticketHolder.Ticket.MaxCost;
			OrchestratorPhase phase = DeterminePhase(_ticketHolder.Ticket);
			_logger.LogDebug("Phase: {Phase} (spent ${Spend:F4})", phase, currentCost);

			if (phase == OrchestratorPhase.Done)
			{
				_logger.LogInformation("Orchestrator completed successfully (spent ${Spend:F4})", currentCost);
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Completed successfully (spent ${currentCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Done");
				await FinalizeConversationsAsync(cancellationToken);
				running = false;
			}
			else if (maxCost > 0 && currentCost >= maxCost)
			{
				_logger.LogWarning("Orchestrator exceeded max cost (${Spend:F4} >= ${Max:F4})", currentCost, maxCost);
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Exceeded max cost (${currentCost:F4} >= ${maxCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed");
				await FinalizeConversationsAsync(cancellationToken);
				running = false;
			}
			else if (phase == OrchestratorPhase.Planning)
			{
				bool success = await RunPlanningAsync(_ticketHolder, cancellationToken);
				if (!success)
				{
					_logger.LogWarning("Orchestrator blocked during planning (spent ${Spend:F4})", currentCost);
					await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Blocked during planning (spent ${currentCost:F4})");
					await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed");
					await FinalizeConversationsAsync(cancellationToken);
					running = false;
				}
			}
			else if (phase == OrchestratorPhase.Working)
			{
				bool success = await RunWorkingAsync(_ticketHolder, cancellationToken);
				if (!success)
				{
					_logger.LogWarning("Orchestrator blocked during work (spent ${Spend:F4})", currentCost);
					await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Blocked during work (spent ${currentCost:F4})");
					await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed");
					await FinalizeConversationsAsync(cancellationToken);
					running = false;
				}
			}
		}
	}

	private async Task FinalizeConversationsAsync(CancellationToken cancellationToken)
	{
		if (_managerConversation != null)
		{
			await _managerLlm.FinalizeConversationAsync(_managerConversation, cancellationToken);
			_managerConversation = null;
		}

		if (_developerConversation != null)
		{
			await _developerLlm.FinalizeConversationAsync(_developerConversation, cancellationToken);
			_developerConversation = null;
		}
	}

	private List<Tool> BuildManagerPlanningTools(ShellTools shellTools, FileTools fileTools, TicketTools ticketTools)
	{
		List<Tool> tools = new List<Tool>();
		shellTools.AddTools(tools, LlmRole.ManagerPlanning);
		fileTools.AddTools(tools, LlmRole.ManagerPlanning);
		ticketTools.AddTools(tools, LlmRole.ManagerPlanning);
		return tools;
	}

	private List<Tool> BuildManagerImplementingTools(ShellTools shellTools, FileTools fileTools, TicketTools ticketTools)
	{
		List<Tool> tools = new List<Tool>();
		shellTools.AddTools(tools, LlmRole.ManagerImplementing);
		fileTools.AddTools(tools, LlmRole.ManagerImplementing);
		ticketTools.AddTools(tools, LlmRole.ManagerImplementing);
		ToolHelper.AddTools(tools, this,
			nameof(StartNextSubtaskAsync),
			nameof(MarkSubtaskCompleteAsync),
			nameof(MarkSubtaskRejectedAsync));
		return tools;
	}

	private List<Tool> BuildDeveloperTools(ShellTools shellTools, FileTools fileTools)
	{
		List<Tool> tools = new List<Tool>();
		shellTools.AddTools(tools, LlmRole.Developer);
		fileTools.AddTools(tools, LlmRole.Developer);
		ToolHelper.AddTools(tools, this,
			nameof(AskForHelpAsync),
			nameof(EndSubtaskAsync));
		return tools;
	}

	private static OrchestratorPhase DeterminePhase(TicketDto ticket)
	{
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

		OrchestratorPhase result = OrchestratorPhase.Working;
		if (!hasSubtasks)
		{
			result = OrchestratorPhase.Planning;
		}
		else if (allComplete)
		{
			result = OrchestratorPhase.Done;
		}

		return result;
	}

	private (string? TaskId, string? TaskName, string? SubtaskId, string? SubtaskName, string? SubtaskDescription) FindNextIncompleteSubtask()
	{
		if (_ticketHolder == null)
		{
			return (null, null, null, null, null);
		}

		foreach (KanbanTaskDto task in _ticketHolder.Ticket.Tasks)
		{
			foreach (KanbanSubtaskDto subtask in task.Subtasks)
			{
				if (subtask.Status != SubtaskStatus.Complete)
				{
					return (task.Id, task.Name, subtask.Id, subtask.Name, subtask.Description);
				}
			}
		}

		return (null, null, null, null, null);
	}

	private async Task<bool> RunPlanningAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		bool success = false;

		_logger.LogInformation("Manager: Planning ticket breakdown");
		await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Planning ticket breakdown");

		string userPrompt = $"""
			Break down this ticket into tasks and subtasks:

			Ticket: {ticketHolder.Ticket.Title}
			Description: {ticketHolder.Ticket.Description}
			""";

		_managerConversation = _managerLlm.CreateConversation(_managerPlanningPrompt, userPrompt);
		LlmResult result = await _managerLlm.ContinueAsync(_managerConversation, _managerPlanningTools!, cancellationToken);
		_logger.LogDebug("Manager planning response: {Response}", result.Content);

		if (result.AccumulatedCost > 0)
		{
			TicketDto? updated = await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
			if (updated != null)
			{
				ticketHolder.Update(updated);
			}
		}

		bool hasSubtasks = ticketHolder.Ticket.Tasks.Any(t => t.Subtasks.Count > 0);

		if (result.Success && hasSubtasks)
		{
			int subtaskCount = ticketHolder.Ticket.Tasks.Sum(t => t.Subtasks.Count);
			_logger.LogInformation("Planning complete: {Count} subtasks created", subtaskCount);
			success = true;
		}
		else if (!result.Success)
		{
			_logger.LogError("Manager LLM failed during planning: {Error}", result.ErrorMessage);
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {result.ErrorMessage}");
		}
		else
		{
			_logger.LogWarning("Planning failed - no subtasks created");
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Failed to plan ticket");
		}

		return success;
	}

	private async Task<bool> RunWorkingAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		bool success = false;

		_logger.LogInformation("Manager: Working on ticket");

		(string? taskId, string? taskName, string? subtaskId, string? subtaskName, string? subtaskDescription) = FindNextIncompleteSubtask();
		string currentSubtaskInfo = subtaskName != null
			? $"Current subtask: '{subtaskName}' in task '{taskName}'\nDescription: {subtaskDescription}"
			: "No incomplete subtasks remaining.";

		string ticketSummary = FormatTicketStatus(ticketHolder.Ticket);

		string userPrompt = $"""
			Continue working on this ticket.

			{ticketSummary}

			{currentSubtaskInfo}
			""";

		if (_managerConversation == null)
		{
			_managerConversation = _managerLlm.CreateConversation(_managerImplementingPrompt, userPrompt);
		}
		else
		{
			_managerConversation.AddUserMessage(userPrompt);
		}

		LlmResult result = await _managerLlm.ContinueAsync(_managerConversation, _managerImplementingTools!, cancellationToken);
		_logger.LogDebug("Manager working response: {Response}", result.Content);

		if (result.AccumulatedCost > 0)
		{
			TicketDto? updated = await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, result.AccumulatedCost);
			if (updated != null)
			{
				ticketHolder.Update(updated);
			}
		}

		if (result.Success)
		{
			success = true;
		}
		else
		{
			_logger.LogError("Manager LLM failed during work: {Error}", result.ErrorMessage);
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {result.ErrorMessage}");
		}

		return success;
	}

	[Description("Start working on the next incomplete subtask. The developer will work autonomously until complete.")]
	public async Task<ToolResult> StartNextSubtaskAsync()
	{
		ToolResult result;

		if (_ticketHolder == null || _workDir == null)
		{
			result = new ToolResult("Error: Orchestrator not initialized");
		}
		else if (_currentSubtaskId != null)
		{
			result = new ToolResult("Error: Already working on a subtask. Complete or reject the current subtask before starting a new one.");
		}
		else
		{
			(string? taskId, string? taskName, string? subtaskId, string? subtaskName, string? subtaskDescription) = FindNextIncompleteSubtask();

			if (subtaskId == null)
			{
				result = new ToolResult("No incomplete subtasks remaining. All work is complete.");
			}
			else
			{
				TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, taskId!, subtaskId, SubtaskStatus.InProgress);
				if (updated != null)
				{
					_ticketHolder.Update(updated);
				}

				_currentTaskId = taskId;
				_currentSubtaskId = subtaskId;

				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Started subtask: {subtaskName}");
				_logger.LogInformation("Started subtask: {SubtaskName}", subtaskName);

				string initialPrompt = $"Work on this subtask: '{subtaskName}' in task '{taskName}'.\n\nDescription: {subtaskDescription}\n\nCall end_subtask when complete, or ask_for_help if you need guidance.";
				_developerConversation = _developerLlm.CreateConversation(_developerPrompt, initialPrompt);

				string developerResult = await RunDeveloperLoopAsync();
				result = new ToolResult(developerResult);
			}
		}

		return result;
	}

	private async Task<string> RunDeveloperLoopAsync()
	{
		string result;

		int iterationCount = 0;
		string? completionSummary = null;

		while (completionSummary == null && iterationCount < CircuitBreakerThreshold)
		{
			LlmResult llmResult = await _developerLlm.ContinueAsync(_developerConversation!, _developerTools!, CancellationToken.None);

			if (llmResult.AccumulatedCost > 0)
			{
				TicketDto? updated = await _apiClient.AddLlmCostAsync(_ticketHolder!.Ticket.Id, llmResult.AccumulatedCost);
				if (updated != null)
				{
					_ticketHolder.Update(updated);
				}
			}

			if (!llmResult.Success)
			{
				completionSummary = $"Developer LLM failed: {llmResult.ErrorMessage}";
			}
			else if (llmResult.FinalToolCalled == "end_subtask")
			{
				completionSummary = llmResult.Content;
			}
			else
			{
				iterationCount++;
				if (iterationCount < CircuitBreakerThreshold)
				{
					_developerConversation!.AddUserMessage("Continue working. Call end_subtask when done, or ask_for_help if you need guidance.");
				}
			}
		}

		if (completionSummary != null)
		{
			await _apiClient.AddActivityLogAsync(_ticketHolder!.Ticket.Id, $"Developer → Manager (SUBTASK COMPLETE): {completionSummary}");
			_logger.LogInformation("Developer → Manager (SUBTASK COMPLETE): {Message}", completionSummary);
			result = $"Developer reports SUBTASK COMPLETE: {completionSummary}\n\nReview the work and use mark_subtask_complete or mark_subtask_rejected.";
		}
		else
		{
			string recentActivity = GetRecentMessagesFromConversation(_developerConversation!, 20);
			await _apiClient.AddActivityLogAsync(_ticketHolder!.Ticket.Id, "Circuit breaker: Developer exceeded iteration limit");
			_logger.LogWarning("Circuit breaker tripped after {Count} iterations", iterationCount);
			result = $"CIRCUIT BREAKER: Developer ran {iterationCount} iterations without completing. Review recent activity:\n\n{recentActivity}";
		}

		return result;
	}

	[Description("Ask the manager for help, clarification, or guidance. The manager will respond and you can continue working.")]
	public async Task<ToolResult> AskForHelpAsync(
		[Description("Your question for the manager")] string question)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(question))
		{
			result = new ToolResult("Error: Question cannot be empty");
		}
		else if (_ticketHolder == null)
		{
			result = new ToolResult("Error: Orchestrator not initialized");
		}
		else if (_managerConversation == null)
		{
			result = new ToolResult("Error: No active manager conversation");
		}
		else
		{
			await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer → Manager (question): {question}");
			_logger.LogInformation("Developer → Manager (question): {Message}", question);

			string userMessage = $"The developer working on the current subtask has a question:\n\n\"{question}\"\n\nProvide a helpful, concise answer.";
			_managerConversation.AddUserMessage(userMessage);

			LlmResult managerResult = await _managerLlm.ContinueAsync(_managerConversation, _managerImplementingTools!, CancellationToken.None);

			if (managerResult.AccumulatedCost > 0)
			{
				TicketDto? updated = await _apiClient.AddLlmCostAsync(_ticketHolder.Ticket.Id, managerResult.AccumulatedCost);
				if (updated != null)
				{
					_ticketHolder.Update(updated);
				}
			}

			if (managerResult.Success && !string.IsNullOrWhiteSpace(managerResult.Content))
			{
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Manager → Developer (answer): {managerResult.Content}");
				_logger.LogInformation("Manager → Developer (answer): {Message}", managerResult.Content);
				result = new ToolResult($"Manager says: {managerResult.Content}");
			}
			else
			{
				result = new ToolResult("Manager was unable to respond. Continue with your best judgment.");
			}
		}

		return result;
	}

	[Description("Signal that you have finished working on the current subtask. Call this when your work is complete.")]
	public Task<ToolResult> EndSubtaskAsync(
		[Description("Summary of what you accomplished")] string summary)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(summary))
		{
			result = new ToolResult("Error: Summary cannot be empty", false);
		}
		else
		{
			result = new ToolResult(summary, true);
		}

		return Task.FromResult(result);
	}

	[Description("Mark the current subtask as complete. Only call this when the subtask work is verified complete.")]
	public async Task<ToolResult> MarkSubtaskCompleteAsync(
		[Description("Summary of what was accomplished")] string notes)
	{
		ToolResult result;

		if (_ticketHolder == null)
		{
			result = new ToolResult("Error: Orchestrator not initialized");
		}
		else if (_currentSubtaskId == null || _currentTaskId == null)
		{
			result = new ToolResult("Error: No subtask is currently assigned. Use start_next_subtask first.");
		}
		else if (string.IsNullOrWhiteSpace(notes))
		{
			result = new ToolResult("Error: Notes are required");
		}
		else
		{
			TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, SubtaskStatus.Complete);
			if (updated != null)
			{
				_ticketHolder.Update(updated);
			}

			await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask completed: {notes}");
			_logger.LogInformation("Subtask completed: {Notes}", notes);

			_currentTaskId = null;
			_currentSubtaskId = null;

			if (_developerConversation != null)
			{
				await _developerLlm.FinalizeConversationAsync(_developerConversation, CancellationToken.None);
				_developerConversation = null;
			}

			result = new ToolResult($"Subtask marked complete. {notes}");
		}

		return result;
	}

	[Description("Mark the current subtask as rejected. Use this if the subtask cannot be completed.")]
	public async Task<ToolResult> MarkSubtaskRejectedAsync(
		[Description("Reason for rejection")] string reason)
	{
		ToolResult result;

		if (_ticketHolder == null)
		{
			result = new ToolResult("Error: Orchestrator not initialized");
		}
		else if (_currentSubtaskId == null || _currentTaskId == null)
		{
			result = new ToolResult("Error: No subtask is currently assigned.");
		}
		else if (string.IsNullOrWhiteSpace(reason))
		{
			result = new ToolResult("Error: Reason is required");
		}
		else
		{
			TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, _currentTaskId, _currentSubtaskId, SubtaskStatus.Rejected);
			if (updated != null)
			{
				_ticketHolder.Update(updated);
			}

			await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask rejected: {reason}");
			_logger.LogWarning("Subtask rejected: {Reason}", reason);

			_currentTaskId = null;
			_currentSubtaskId = null;

			if (_developerConversation != null)
			{
				await _developerLlm.FinalizeConversationAsync(_developerConversation, CancellationToken.None);
				_developerConversation = null;
			}

			result = new ToolResult($"Subtask rejected. {reason}");
		}

		return result;
	}

	private static string GetRecentMessagesFromConversation(LlmConversation conversation, int count)
	{
		List<ChatMessage> messages = conversation.Messages;
		int startIndex = Math.Max(0, messages.Count - count);
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"Last {Math.Min(count, messages.Count - startIndex)} messages:");

		for (int i = startIndex; i < messages.Count; i++)
		{
			ChatMessage msg = messages[i];
			string content = msg.Content ?? string.Empty;

			if (msg.Role == "assistant")
			{
				if (msg.ToolCalls != null && msg.ToolCalls.Count > 0)
				{
					foreach (ToolCallMessage tc in msg.ToolCalls)
					{
						sb.AppendLine($"  [assistant] {tc.Function.Name}({tc.Function.Arguments})");
					}
				}
				else
				{
					sb.AppendLine($"  [assistant] {content}");
				}
			}
			else if (msg.Role == "tool")
			{
				sb.AppendLine($"  [tool result] {content}");
			}
		}

		return sb.ToString();
	}

	private static string FormatTicketStatus(TicketDto ticket)
	{
		StringBuilder sb = new StringBuilder();
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
