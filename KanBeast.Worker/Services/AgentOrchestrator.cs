using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

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

		if (await RunPlanningAsync(_ticketHolder, cancellationToken))
		{
			if (await RunWorkingAsync(_ticketHolder, cancellationToken))
			{
				decimal finalCost = _ticketHolder.Ticket.LlmCost;
				_logger.LogInformation("Orchestrator completed successfully (spent ${Spend:F4})", finalCost);
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Completed successfully (spent ${finalCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Done");
			}
			else
			{
				decimal currentCost = _ticketHolder.Ticket.LlmCost;
				_logger.LogWarning("Orchestrator blocked during work (spent ${Spend:F4})", currentCost);
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Blocked during work (spent ${currentCost:F4})");
				await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed");
			}
		}
		else
		{
			decimal currentCost = _ticketHolder.Ticket.LlmCost;
			_logger.LogWarning("Orchestrator blocked during planning (spent ${Spend:F4})", currentCost);
			await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Blocked during planning (spent ${currentCost:F4})");
			await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed");
		}

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
		ToolHelper.AddTools(tools, this,
			nameof(PlanningCompleteAsync),
			nameof(DeleteAllTasksAsync));
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

	private static decimal GetRemainingBudget(TicketHolder ticketHolder)
	{
		decimal maxCost = ticketHolder.Ticket.MaxCost;
		if (maxCost <= 0)
		{
			return 0;
		}

		decimal currentCost = ticketHolder.Ticket.LlmCost;
		decimal remaining = maxCost - currentCost;
		return remaining > 0 ? remaining : 0;
	}

	[Description("Signal that planning is complete and implementation should begin. Call this when all tasks and subtasks have been created.")]
	public Task<ToolResult> PlanningCompleteAsync()
	{
		ToolResult result = new ToolResult("Planning complete. Beginning implementation phase.", true, "planning_complete");
		return Task.FromResult(result);
	}

	[Description("Delete all tasks and subtasks to start planning over. Use this if the current plan is fundamentally wrong.")]
	public async Task<ToolResult> DeleteAllTasksAsync()
	{
		ToolResult result;

		if (_ticketHolder == null)
		{
			result = new ToolResult("Error: Orchestrator not initialized");
		}
		else
		{
			TicketDto? updated = await _apiClient.DeleteAllTasksAsync(_ticketHolder.Ticket.Id);
			if (updated != null)
			{
				_ticketHolder.Update(updated);
				_logger.LogInformation("All tasks deleted, starting planning over");
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, "Manager: Deleted all tasks to restart planning");
				result = new ToolResult("All tasks and subtasks deleted. You can now create a new plan.");
			}
			else
			{
				result = new ToolResult("Error: Failed to delete tasks");
			}
		}

		return result;
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
			List<(string TaskId, string TaskName, string SubtaskId, string SubtaskName, string SubtaskDescription)> subtasks = _ticketHolder.Ticket.GetIncompleteSubtasks();

			if (subtasks.Count == 0)
			{
				result = new ToolResult("No incomplete subtasks remaining. All work is complete.");
			}
			else
			{
				(string taskId, string taskName, string subtaskId, string subtaskName, string subtaskDescription) = subtasks[0];

				TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, taskId, subtaskId, SubtaskStatus.InProgress);
				if (updated != null)
				{
					_ticketHolder.Update(updated);
				}

				_currentTaskId = taskId;
				_currentSubtaskId = subtaskId;

				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Started subtask: {subtaskName}");
				_logger.LogInformation("Started subtask: {SubtaskName}", subtaskName);

				if (_developerConversation != null)
				{
					// Reuse existing conversation for rejected subtask retry
					_developerConversation.AddUserMessage($"The manager rejected your previous work on this subtask. Try again.\n\nSubtask: '{subtaskName}' in task '{taskName}'\nDescription: {subtaskDescription}\n\nCall end_subtask when complete, or ask_for_help if you need guidance.");
				}
				else
				{
					string initialPrompt = $"Work on this subtask: '{subtaskName}' in task '{taskName}'.\n\nDescription: {subtaskDescription}\n\nCall end_subtask when complete, or ask_for_help if you need guidance.";
					_developerConversation = _developerLlm.CreateConversation(_developerPrompt, initialPrompt);
				}

				string developerResult = await RunDeveloperLoopAsync();
				result = new ToolResult(developerResult);
			}
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

			decimal remainingBudget = GetRemainingBudget(_ticketHolder);
			LlmResult managerResult = await _managerLlm.ContinueAsync(_managerConversation, _managerImplementingTools!, remainingBudget, CancellationToken.None);

			if (managerResult.AccumulatedCost > 0)
			{
				TicketDto? updated = await _apiClient.AddLlmCostAsync(_ticketHolder.Ticket.Id, managerResult.AccumulatedCost);
				if (updated != null)
				{
					_ticketHolder.Update(updated);
				}
			}

			if (managerResult.ExitReason == LlmExitReason.Completed && !string.IsNullOrWhiteSpace(managerResult.Content))
			{
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Manager → Developer (answer): {managerResult.Content}");
				_logger.LogInformation("Manager → Developer (answer): {Message}", managerResult.Content);
				result = new ToolResult($"Manager says: {managerResult.Content}");
			}
			else if (managerResult.ExitReason == LlmExitReason.LlmCallFailed)
			{
				_logger.LogError("Manager LLM failed while answering question: {Error}", managerResult.ErrorMessage);
				result = new ToolResult("Manager LLM is unavailable. Continue with your best judgment.");
			}
			else if (managerResult.ExitReason == LlmExitReason.CostExceeded)
			{
				result = new ToolResult("Cost budget exceeded. Continue with your best judgment.");
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

			// Tell the developer why their work was rejected so they can fix it on retry
			if (_developerConversation != null)
			{
				_developerConversation.AddUserMessage($"The manager rejected your work. Reason: {reason}");
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

	//-------------------

	private async Task<bool> RunPlanningAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		bool result = false;

		if (ticketHolder.Ticket.HasValidPlan())
		{
			result = true;
		}
		else
		{
			_logger.LogInformation("Manager: Planning ticket breakdown");
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Planning ticket breakdown");

			string userPrompt = $"""
				Break down this ticket into tasks and subtasks.  Every task must have at least one subtask.  If you don't like this plan, you can call delete_all_tasks to start over.

				Ticket: {ticketHolder.Ticket.Title}
				Description: {ticketHolder.Ticket.Description}

				When planning is complete, call planning_complete to begin implementation.
				""";

			_managerConversation = _managerLlm.CreateConversation(_managerPlanningPrompt, userPrompt);

			for (;;)
			{
				decimal remainingBudget = GetRemainingBudget(ticketHolder);
				LlmResult llmResult = await _managerLlm.ContinueAsync(_managerConversation, _managerPlanningTools!, remainingBudget, cancellationToken);
				_logger.LogDebug("Manager planning response: {Response}", llmResult.Content);

				if (llmResult.AccumulatedCost > 0)
				{
					TicketDto? updated = await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, llmResult.AccumulatedCost);
					if (updated != null)
					{
						ticketHolder.Update(updated);
					}
				}

				if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "planning_complete")
				{
					if (ticketHolder.Ticket.HasValidPlan())
					{
						int subtaskCount = 0;
						foreach (KanbanTaskDto task in ticketHolder.Ticket.Tasks)
						{
							subtaskCount += task.Subtasks.Count;
						}
						_logger.LogInformation("Planning complete: {Count} subtasks created", subtaskCount);
						result = true;
						break;
					}
					else
					{
						_logger.LogWarning("Planning incomplete - not all tasks have subtasks");
						_managerConversation.AddUserMessage("Planning is not complete. Every task must have at least one subtask. Review your tasks and add subtasks where missing, then call planning_complete again.");
					}
				}
				else if (llmResult.ExitReason == LlmExitReason.Completed)  // to avoid partially completed planning phases, the LLM must call planning_complete to move on to the next phase.
				{
					_managerConversation.AddUserMessage("Continue planning by adding tasks and subtasks. When finished, call planning_complete to begin implementation.");
				}
				else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
				{
					_logger.LogWarning("Manager planning hit iteration limit, prompting to continue");
					_managerConversation.AddUserMessage("Are you making good progress? Keep adding tasks and subtasks as needed.  You must call planning_complete when ready to move onto implementation.");
				}
				else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
				{
					_logger.LogWarning("Manager planning exceeded cost budget");
					await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Cost budget exceeded during planning");
					break;
				}
				else
				{
					_logger.LogError("Manager LLM failed during planning: {Error}", llmResult.ErrorMessage);
					await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {llmResult.ErrorMessage}");
					break;
				}
			}
		}

		return result;
	}

	private async Task<bool> RunWorkingAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Manager: Working on ticket");

		List<(string TaskId, string TaskName, string SubtaskId, string SubtaskName, string SubtaskDescription)> subtasks = ticketHolder.Ticket.GetIncompleteSubtasks();

		foreach ((string taskId, string taskName, string subtaskId, string subtaskName, string subtaskDescription) in subtasks)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Start fresh conversations for each subtask
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

			string ticketSummary = FormatTicketStatus(ticketHolder.Ticket);
			string currentSubtaskInfo = $"Current subtask: '{subtaskName}' in task '{taskName}'\nDescription: {subtaskDescription}";

			string userPrompt = $"""
				Work on this subtask until it is complete.

				{ticketSummary}

				{currentSubtaskInfo}

				Call start_next_subtask to begin work on this subtask.
				""";

			_managerConversation = _managerLlm.CreateConversation(_managerImplementingPrompt, userPrompt);

			// Work on this subtask until complete or failed
			for (;;)
			{
				decimal remainingBudget = GetRemainingBudget(ticketHolder);
				LlmResult llmResult = await _managerLlm.ContinueAsync(_managerConversation, _managerImplementingTools!, remainingBudget, cancellationToken);
				_logger.LogDebug("Manager working response: {Response}", llmResult.Content);

				if (llmResult.AccumulatedCost > 0)
				{
					TicketDto? updated = await _apiClient.AddLlmCostAsync(ticketHolder.Ticket.Id, llmResult.AccumulatedCost);
					if (updated != null)
					{
						ticketHolder.Update(updated);
					}
				}

				if (llmResult.ExitReason == LlmExitReason.Completed || llmResult.ExitReason == LlmExitReason.ToolRequestedExit)
				{
					// MarkSubtaskCompleteAsync sets _currentSubtaskId to null when done
					if (_currentSubtaskId == null)
					{
						break;
					}
					// Otherwise keep working (e.g., after rejection and retry)
				}
				else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
				{
					_logger.LogWarning("Manager working hit iteration limit, prompting to continue");
					_managerConversation.AddUserMessage("Continue working on the current subtask. Use start_next_subtask if not already started.");
				}
				else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
				{
					_logger.LogWarning("Manager working exceeded cost budget");
					await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Cost budget exceeded during work");
					return false;
				}
				else
				{
					_logger.LogError("Manager LLM failed during work: {Error}", llmResult.ErrorMessage);
					await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {llmResult.ErrorMessage}");
					return false;
				}
			}
		}

		return true;
	}

	private async Task<string> RunDeveloperLoopAsync()
	{
		string result;
		int iterationCount = 0;

		for (;;)
		{
			if (iterationCount >= CircuitBreakerThreshold)
			{
				string recentActivity = GetRecentMessagesFromConversation(_developerConversation!, 20);
				await _apiClient.AddActivityLogAsync(_ticketHolder!.Ticket.Id, "Circuit breaker: Developer exceeded iteration limit");
				_logger.LogWarning("Circuit breaker tripped after {Count} iterations", iterationCount);
				result = $"CIRCUIT BREAKER: Developer ran {iterationCount} iterations without completing. Review recent activity:\n\n{recentActivity}";
				break;
			}

			decimal remainingBudget = GetRemainingBudget(_ticketHolder!);
			LlmResult llmResult = await _developerLlm.ContinueAsync(_developerConversation!, _developerTools!, remainingBudget, CancellationToken.None);

			if (llmResult.AccumulatedCost > 0)
			{
				TicketDto? updated = await _apiClient.AddLlmCostAsync(_ticketHolder!.Ticket.Id, llmResult.AccumulatedCost);
				if (updated != null)
				{
					_ticketHolder.Update(updated);
				}
			}

			if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "end_subtask")
			{
				await _apiClient.AddActivityLogAsync(_ticketHolder!.Ticket.Id, $"Developer → Manager (SUBTASK COMPLETE): {llmResult.Content}");
				_logger.LogInformation("Developer → Manager (SUBTASK COMPLETE): {Message}", llmResult.Content);
				result = $"Developer reports SUBTASK COMPLETE: {llmResult.Content}\n\nReview the work and use mark_subtask_complete or mark_subtask_rejected.";
				break;
			}
			else if (llmResult.ExitReason == LlmExitReason.Completed)
			{
				iterationCount++;
				_developerConversation!.AddUserMessage("Continue working. Call end_subtask when done, or ask_for_help if you need guidance.");
			}
			else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				iterationCount++;
				_developerConversation!.AddUserMessage("Continue working. Call end_subtask when done, or ask_for_help if you need guidance.");
			}
			else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
			{
				await _apiClient.AddActivityLogAsync(_ticketHolder!.Ticket.Id, "Developer: Cost budget exceeded");
				_logger.LogWarning("Developer exceeded cost budget");
				result = "Developer stopped: Cost budget exceeded.";
				break;
			}
			else
			{
				await _apiClient.AddActivityLogAsync(_ticketHolder!.Ticket.Id, $"Developer LLM failed: {llmResult.ErrorMessage}");
				_logger.LogError("Developer LLM failed: {Error}", llmResult.ErrorMessage);
				result = $"Developer LLM failed: {llmResult.ErrorMessage}";
				break;
			}
		}

		return result;
	}
}
