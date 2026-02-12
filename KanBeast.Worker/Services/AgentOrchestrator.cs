using System.ComponentModel;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Result from QA review.
public enum QaVerdict
{
	Approved,
	Rejected
}

public readonly struct QaResult
{
	public QaVerdict Verdict { get; init; }
	public string Message { get; init; }

	public QaResult(QaVerdict verdict, string message)
	{
		Verdict = verdict;
		Message = message;
	}
}

// Coordinates manager LLM with developer LLM.
public class AgentOrchestrator
{
	private readonly ILogger<AgentOrchestrator> _logger;
	private readonly IKanbanApiClient _apiClient;
	private readonly LlmProxy _llmProxy;
	private readonly Dictionary<string, string> _prompts;
	private readonly CompactionSettings _compactionSettings;
	private readonly List<LLMConfig> _llmConfigs;

	private LlmConversation? _developerConversation;

	private TicketHolder? _ticketHolder;
	private string? _workDir;
	private string? _currentTaskId;
	private string? _currentSubtaskId;

	public AgentOrchestrator(
		ILogger<AgentOrchestrator> logger,
		IKanbanApiClient apiClient,
		LlmProxy llmProxy,
		Dictionary<string, string> prompts,
		CompactionSettings compactionSettings,
		List<LLMConfig> llmConfigs,
		TicketHolder ticketHolder,
		string workDir)
	{
		_logger = logger;
		_apiClient = apiClient;
		_llmProxy = llmProxy;
		_prompts = prompts;
		_compactionSettings = compactionSettings;
		_llmConfigs = llmConfigs;
		_ticketHolder = ticketHolder;
		_workDir = workDir;
		ToolsFactory.AddOrchestratorTools(this);
	}

	public async Task StartAgents(TicketHolder ticketHolder, string workDir, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Orchestrator starting for ticket: {Title}", ticketHolder.Ticket.Title);
		await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Starting work", cancellationToken);

		_ticketHolder = ticketHolder;
		_workDir = workDir;

		if (await RunPlanningAsync(_ticketHolder, cancellationToken))
		{
			if (await RunDeveloperAsync(_ticketHolder, cancellationToken))
			{
				decimal finalCost = _ticketHolder.Ticket.LlmCost;
				_logger.LogInformation("Orchestrator completed successfully (spent ${Spend:F4})", finalCost);
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Completed successfully (spent ${finalCost:F4})", cancellationToken);
				await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Done", cancellationToken);
			}
			else
			{
				decimal currentCost = _ticketHolder.Ticket.LlmCost;
				_logger.LogWarning("Orchestrator blocked during work (spent ${Spend:F4})", currentCost);
				await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Blocked during work (spent ${currentCost:F4})", cancellationToken);
				await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed", cancellationToken);
			}
		}
		else
		{
			decimal currentCost = _ticketHolder.Ticket.LlmCost;
			_logger.LogWarning("Orchestrator blocked during planning (spent ${Spend:F4})", currentCost);
			await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Orchestrator: Blocked during planning (spent ${currentCost:F4})", cancellationToken);
			await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, "Failed", cancellationToken);
		}

		if (_developerConversation != null)
		{
			await _developerConversation.FinalizeAsync(cancellationToken);
			_developerConversation = null;
		}
	}

	private ICompaction CreateCompaction()
	{
		ICompaction compaction = new CompactionNone();

		if (string.Equals(_compactionSettings.Type, "summarize", StringComparison.OrdinalIgnoreCase) && _llmConfigs.Count > 0)
		{
			compaction = new CompactionSummarizer(_prompts["compaction"], _llmProxy, _compactionSettings.ContextSizePercent);
		}

		return compaction;
	}

	[Description("Signal that planning is complete and implementation should begin. Call this when all tasks and subtasks have been created.")]
	public static Task<ToolResult> PlanningCompleteAsync(ToolContext context)
	{
		ToolResult result = new ToolResult("Planning complete. Beginning implementation phase.", true, "planning_complete");
		return Task.FromResult(result);
	}

	[Description("Delete all tasks and subtasks to start planning over. Use this if the current plan is fundamentally wrong.")]
	public static async Task<ToolResult> DeleteAllTasksAsync(ToolContext context)
	{
		ToolResult result;

		TicketDto? updated = await context.ApiClient!.DeleteAllTasksAsync(context.TicketHolder!.Ticket.Id, context.CancellationToken);
		if (updated != null)
		{
			context.TicketHolder.Update(updated);
			await context.ApiClient.AddActivityLogAsync(context.TicketHolder.Ticket.Id, "Manager: Deleted all tasks to restart planning", context.CancellationToken);
			result = new ToolResult("All tasks and subtasks deleted. You can now create a new plan.");
		}
		else
		{
			result = new ToolResult("Error: Failed to delete tasks");
		}

		return result;
	}

	[Description("Signal that you have finished working on the current subtask. Call this when your work is complete.")]
	public async Task<ToolResult> EndSubtaskAsync(
		[Description("Summary of what you accomplished")] string summary,
		ToolContext context)
	{
		ToolResult result;
		CancellationToken cancellationToken = context.CancellationToken;

		if (string.IsNullOrWhiteSpace(summary))
		{
			result = new ToolResult("Error: Summary cannot be empty", false);
		}
		else if (context.CurrentTaskId == null || context.CurrentSubtaskId == null)
		{
			result = new ToolResult("Error: No active subtask", false);
		}
		else
		{
			QaResult qaResult = await RunQaAsync(summary, _developerConversation!.Memories, cancellationToken);

			if (qaResult.Verdict == QaVerdict.Approved)
			{
				// Compact the developer conversation to hoist memories before it is discarded.
				await _developerConversation.CompactNowAsync(cancellationToken);

				TicketDto? updated = await context.ApiClient!.UpdateSubtaskStatusAsync(context.TicketHolder!.Ticket.Id, context.CurrentTaskId, context.CurrentSubtaskId, SubtaskStatus.Complete, cancellationToken);
				if (updated != null)
				{
					context.TicketHolder.Update(updated);
				}

				await context.ApiClient.AddActivityLogAsync(context.TicketHolder!.Ticket.Id, $"Subtask completed: {summary}", cancellationToken);
				_logger.LogInformation("Subtask completed");

				result = new ToolResult("Subtask approved and marked complete.", true, "end_subtask");
			}
			else
			{
				await context.ApiClient!.AddActivityLogAsync(context.TicketHolder!.Ticket.Id, $"QA rejected: {qaResult.Message}", cancellationToken);
				_logger.LogWarning("QA rejected, developer will retry: {Feedback}", qaResult.Message);

				result = new ToolResult($"QA rejected your work. Feedback: {qaResult.Message}\n\nPlease fix the issues and call end_subtask tool when done.", false);
			}
		}

		return result;
	}

	[Description("Approve the developer's work on this subtask. Call this when the work meets the acceptance criteria.")]
	public static Task<ToolResult> ApproveSubtaskAsync(
		[Description("Summary of what was verified")] string notes,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(notes))
		{
			result = new ToolResult("Error: Notes are required", false);
		}
		else
		{
			result = new ToolResult(notes, true, "approve_subtask");
		}

		return Task.FromResult(result);
	}

	[Description("Reject the developer's work on this subtask. The developer will retry with your feedback.")]
	public static Task<ToolResult> RejectSubtaskAsync(
		[Description("Specific feedback on what needs to be fixed")] string feedback,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(feedback))
		{
			result = new ToolResult("Error: Feedback is required", false);
		}
		else
		{
			result = new ToolResult(feedback, true, "reject_subtask");
		}

		return Task.FromResult(result);
	}

	//-------------------

	private async Task<bool> RunPlanningAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Manager: Planning ticket breakdown");
		await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Planning ticket breakdown", cancellationToken);

		string userPrompt = $"""
			Ticket: {ticketHolder.Ticket.Title}
			Description: {ticketHolder.Ticket.Description}
			""";

		ICompaction planningCompaction = CreateCompaction();
		ToolContext planningContext = new ToolContext(_apiClient, _ticketHolder, _workDir!, null, _currentTaskId, _currentSubtaskId, null, cancellationToken);
		LlmConversation planningConversation = new LlmConversation(_prompts["planning"], userPrompt, new LlmMemories(), LlmRole.Planning, planningContext, planningCompaction, false, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-plan");

		bool result = ticketHolder.Ticket.HasValidPlan();

		while (result == false)
		{
			LlmResult llmResult = await _llmProxy.ContinueAsync(planningConversation, 16384, cancellationToken);
			_logger.LogDebug("Manager planning response: {Response}", llmResult.Content);

			if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "planning_complete")
			{
				result = true;
				break;
			}
			else if (llmResult.ExitReason == LlmExitReason.Completed)
			{
				await planningConversation.AddUserMessageAsync("Continue planning by adding tasks and subtasks. When finished, call planning_complete tool to begin implementation.", cancellationToken);
			}
			else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				_logger.LogWarning("Manager planning hit iteration limit, prompting to continue");

				planningConversation.ResetIteration();

				await planningConversation.AddUserMessageAsync("Are you making good progress? Keep adding tasks and subtasks as needed.  You must call planning_complete tool when ready to move onto implementation.", cancellationToken);
			}
			else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
			{
				_logger.LogWarning("Manager planning exceeded cost budget");
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Cost budget exceeded during planning", cancellationToken);
				break;
			}
			else
			{
				_logger.LogError("Manager LLM failed during planning: {Error}", llmResult.ErrorMessage);
				await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {llmResult.ErrorMessage}", cancellationToken);
				break;
			}
		}

		if (result)
		{
			int subtaskCount = 0;
			foreach (KanbanTaskDto task in ticketHolder.Ticket.Tasks)
			{
				subtaskCount += task.Subtasks.Count;
			}
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning complete.", cancellationToken);
			_logger.LogInformation($"Planning complete: {subtaskCount} subtasks created");
		}
		else
		{
			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning failed.", cancellationToken);
			_logger.LogInformation("Planning failed.");
		}

		return result;
	}

	private async Task<bool> RunDeveloperAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		const int ProgressCheckThreshold = 3;  // 3 times that the LLM has returned without calling the end_subtask tool (or just has had a ton of tool calls).  This is an indication something is probably wrong.
		const int ContextResetThreshold  = 7;  // 7 times is a whole lot.  Wipe the context and start fresh.

		_logger.LogInformation("Working on ticket");

		LlmMemories memories = new LlmMemories();
		List<(string TaskId, string TaskName, string SubtaskId, string SubtaskName, string SubtaskDescription)> subtasks = ticketHolder.Ticket.GetIncompleteSubtasks();

		foreach ((string taskId, string taskName, string subtaskId, string subtaskName, string subtaskDescription) in subtasks)
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (_developerConversation != null)
			{
				await _developerConversation.FinalizeAsync(cancellationToken);
				_developerConversation = null;
			}

			// Try primary LLM again for each new subtask
			_llmProxy.ResetFallback();

			_currentTaskId = taskId;
			_currentSubtaskId = subtaskId;

			TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, taskId, subtaskId, SubtaskStatus.InProgress, cancellationToken);
			if (updated != null)
			{
				ticketHolder.Update(updated);
			}

			await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Started subtask: {subtaskName}", cancellationToken);
			_logger.LogInformation("Started subtask: {SubtaskName}", subtaskName);

			string initialPrompt = $"Work on this subtask: '{subtaskName}' in task '{taskName}'.\n\nDescription: {subtaskDescription}\n\nCall end_subtask tool when complete.";
			ICompaction developerCompaction = CreateCompaction();

			ToolContext devContext = new ToolContext(_apiClient, _ticketHolder, _workDir!, null, _currentTaskId, _currentSubtaskId, null, cancellationToken);
			_developerConversation = new LlmConversation(_prompts["developer"], initialPrompt, memories, LlmRole.Developer, devContext, developerCompaction, false, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-dev");

			int iterationCount = 0;
			bool subtaskComplete = false;
			bool blocked = false;

			for (;;)
			{
				LlmResult llmResult = await _llmProxy.ContinueAsync(_developerConversation, null, cancellationToken);

				if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "end_subtask")
				{
					subtaskComplete = true;
					break;
				}
				else if (llmResult.ExitReason == LlmExitReason.Completed || llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
				{
					iterationCount++;

					_developerConversation.ResetIteration();

					if (iterationCount >= ContextResetThreshold)
					{
						await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Context reset: Developer exceeded iteration limit, starting fresh", cancellationToken);
						_logger.LogWarning("Context reset after {Count} iterations", iterationCount);

						await _developerConversation.FinalizeAsync(cancellationToken);
						string continuePrompt = $"""
							You were working on subtask '{subtaskName}' but got stuck. Look at the local changes and decide if you should continue or take a fresh approach.
							Description: {subtaskDescription}
							Call end_subtask tool when complete.
							""";
						ICompaction continueCompaction = CreateCompaction();

						ToolContext continueContext = new ToolContext(_apiClient, _ticketHolder, _workDir!, null, _currentTaskId, _currentSubtaskId, null, cancellationToken);
						_developerConversation = new LlmConversation(_prompts["developer"], continuePrompt, memories, LlmRole.Developer, continueContext, continueCompaction, false, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-dev");
						iterationCount = 0;
					}
					else if (iterationCount == ProgressCheckThreshold)
					{
						_logger.LogWarning("Progress check at {Count} iterations", iterationCount);

						await _developerConversation.AddUserMessageAsync("This is god speaking. You've been working for a while. Are you making progress? If you're stuck, try a different approach. Call end_subtask tool when done.", cancellationToken);
					}
					else
					{
						await _developerConversation.AddUserMessageAsync("This is god speaking. Continue working or call end_subtask tool when done.", cancellationToken);
					}
				}
				else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
				{
					await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Developer: Cost budget exceeded", cancellationToken);
					_logger.LogWarning("Developer exceeded cost budget");
					blocked = true;
					break;
				}
				else
				{
					await _apiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Developer LLM failed: {llmResult.ErrorMessage}", cancellationToken);
					_logger.LogError("Developer LLM failed: {Error}", llmResult.ErrorMessage);
					blocked = true;
					break;
				}
			}

			_currentTaskId = null;
			_currentSubtaskId = null;

			if (blocked)
			{
				return false;
			}

			if (!subtaskComplete)
			{
				return false;
			}
		}

		return true;
	}

	private async Task<QaResult> RunQaAsync(string developerSummary, LlmMemories memories, CancellationToken cancellationToken)
	{
		string userPrompt = $"""
			Review the developer's work on this subtask.

			Developer's summary: {developerSummary}

			Verify the work meets the acceptance criteria. Call approve_subtask tool if the work is complete and verified. Call reject_subtask tool with specific feedback if changes are needed, or if you are unable to review the work for some reason.
			""";


		// Start fresh conversations for each subtask
		ICompaction qaCompaction = CreateCompaction();
		ToolContext qaContext = new ToolContext(_apiClient, _ticketHolder, _workDir!, null, _currentTaskId, _currentSubtaskId, null, cancellationToken);
		LlmConversation? qaConversation = new LlmConversation(_prompts["qualityassurance"], userPrompt, memories, LlmRole.QA, qaContext, qaCompaction, false, "/workspace/logs", $"TIK-{_ticketHolder!.Ticket.Id}-qa");

		try
		{
			for (;;)
			{
				LlmResult llmResult = await _llmProxy.ContinueAsync(qaConversation, 8192, cancellationToken);

				if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit)
				{
					if (llmResult.FinalToolCalled == "approve_subtask")
					{
						_logger.LogInformation("QA approved: {Message}", llmResult.Content);
						await qaConversation.CompactNowAsync(cancellationToken);
						return new QaResult(QaVerdict.Approved, llmResult.Content);
					}
					else if (llmResult.FinalToolCalled == "reject_subtask")
					{
						_logger.LogInformation("QA rejected: {Message}", llmResult.Content);
						await qaConversation.CompactNowAsync(cancellationToken);
						return new QaResult(QaVerdict.Rejected, llmResult.Content);
					}
				}
				else if (llmResult.ExitReason == LlmExitReason.Completed || llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
				{
					qaConversation.ResetIteration();

					await qaConversation.AddUserMessageAsync("Please review the work and call approve_subtask tool.  If you are unable to review the work or it does not meet acceptance criteria, call reject_subtask tool.", cancellationToken);
				}
				else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
				{
					_logger.LogWarning("QA exceeded cost budget");
					return new QaResult(QaVerdict.Rejected, "QA review could not complete due to cost budget. Please verify your work is complete and try again.");
				}
				else
				{
					_logger.LogError("QA LLM failed: {Error}", llmResult.ErrorMessage);
					return new QaResult(QaVerdict.Rejected, $"QA review failed: {llmResult.ErrorMessage ?? "Unknown error"}. Please verify your work is complete and try again.");
				}
			}
		}
		finally
		{
			await qaConversation.FinalizeAsync(cancellationToken);
		}
	}
}
