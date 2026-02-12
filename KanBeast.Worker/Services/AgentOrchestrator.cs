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
	private readonly CompactionSettings _compactionSettings;
	private readonly List<LLMConfig> _llmConfigs;

	private LlmConversation? _developerConversation;

	private string? _currentTaskId;
	private string? _currentSubtaskId;

	public AgentOrchestrator(
		ILogger<AgentOrchestrator> logger,
		CompactionSettings compactionSettings,
		List<LLMConfig> llmConfigs)
	{
		_logger = logger;
		_compactionSettings = compactionSettings;
		_llmConfigs = llmConfigs;
	}

	public async Task StartAgents(TicketHolder ticketHolder, string workDir, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Orchestrator starting for ticket: {Title}", ticketHolder.Ticket.Title);
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Starting work", cancellationToken);

		LlmMemories memories = new LlmMemories();
		if (await RunPlanningAsync(ticketHolder, memories, cancellationToken))
		{
			if (await RunDeveloperAsync(ticketHolder, memories, cancellationToken))
			{
				decimal finalCost = ticketHolder.Ticket.LlmCost;
				_logger.LogInformation("Orchestrator completed successfully (spent ${Spend:F4})", finalCost);
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Completed successfully (spent ${finalCost:F4})", cancellationToken);
				await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Done", cancellationToken);
			}
			else
			{
				decimal currentCost = ticketHolder.Ticket.LlmCost;
				_logger.LogWarning("Orchestrator blocked during work (spent ${Spend:F4})", currentCost);
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Blocked during work (spent ${currentCost:F4})", cancellationToken);
				await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed", cancellationToken);
			}
		}
		else
		{
			decimal currentCost = ticketHolder.Ticket.LlmCost;
			_logger.LogWarning("Orchestrator blocked during planning (spent ${Spend:F4})", currentCost);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Blocked during planning (spent ${currentCost:F4})", cancellationToken);
			await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed", cancellationToken);
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
			compaction = new CompactionSummarizer(WorkerSession.Prompts["compaction"], WorkerSession.LlmProxy, _compactionSettings.ContextSizePercent);
		}

		return compaction;
	}

	//-------------------

	private async Task<bool> RunPlanningAsync(TicketHolder ticketHolder, LlmMemories planningMemories, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Manager: Planning ticket breakdown");
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Planning ticket breakdown", cancellationToken);

		string userPrompt = $"""
			Ticket: {ticketHolder.Ticket.Title}
			Description: {ticketHolder.Ticket.Description}
			""";

		ICompaction planningCompaction = CreateCompaction();
		ToolContext planningContext = new ToolContext(null, _currentTaskId, _currentSubtaskId, planningMemories);
		LlmConversation planningConversation = new LlmConversation(WorkerSession.Prompts["planning"], userPrompt, planningMemories, LlmRole.Planning, planningContext, planningCompaction, false, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-plan");

		bool result = ticketHolder.Ticket.HasValidPlan();

		while (result == false)
		{
			LlmResult llmResult = await WorkerSession.LlmProxy.ContinueAsync(planningConversation, 16384, cancellationToken);
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
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Manager: Cost budget exceeded during planning", cancellationToken);
				break;
			}
			else
			{
				_logger.LogError("Manager LLM failed during planning: {Error}", llmResult.ErrorMessage);
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Manager LLM failed: {llmResult.ErrorMessage}", cancellationToken);
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
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning complete.", cancellationToken);
			await planningConversation.CompactNowAsync(cancellationToken);
			_logger.LogInformation($"Planning complete: {subtaskCount} subtasks created");
		}
		else
		{
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning failed.", cancellationToken);
			_logger.LogInformation("Planning failed.");
		}

		return result;
	}

	private async Task<bool> RunDeveloperAsync(TicketHolder ticketHolder, LlmMemories memories, CancellationToken cancellationToken)
	{
		const int ProgressCheckThreshold = 3;  // 3 times that the LLM has returned without calling the end_subtask tool (or just has had a ton of tool calls).  This is an indication something is probably wrong.
		const int ContextResetThreshold  = 7;  // 7 times is a whole lot.  Wipe the context and start fresh.

		_logger.LogInformation("Working on ticket");

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
			WorkerSession.LlmProxy.ResetFallback();

			_currentTaskId = taskId;
			_currentSubtaskId = subtaskId;

			TicketDto? updated = await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, taskId, subtaskId, SubtaskStatus.InProgress, cancellationToken);
			if (updated != null)
			{
				ticketHolder.Update(updated);
			}

			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Started subtask: {subtaskName}", cancellationToken);
			_logger.LogInformation("Started subtask: {SubtaskName}", subtaskName);

			string initialPrompt = $"Work on this subtask: '{subtaskName}' in task '{taskName}'.\n\nDescription: {subtaskDescription}\n\nCall end_subtask tool when complete.";
			ICompaction developerCompaction = CreateCompaction();

			ToolContext devContext = new ToolContext(null, _currentTaskId, _currentSubtaskId, memories);
			_developerConversation = new LlmConversation(WorkerSession.Prompts["developer"], initialPrompt, memories, LlmRole.Developer, devContext, developerCompaction, false, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-dev");

			int iterationCount = 0;
			bool subtaskComplete = false;
			bool blocked = false;

			for (;;)
			{
				LlmResult llmResult = await WorkerSession.LlmProxy.ContinueAsync(_developerConversation, null, cancellationToken);

				if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "end_subtask")
				{
					string summary = llmResult.Content;
					QaResult qaResult = await RunQaAsync(summary, memories, cancellationToken);

					if (qaResult.Verdict == QaVerdict.Approved)
					{
						await _developerConversation.CompactNowAsync(cancellationToken);

						TicketDto? subtaskUpdate = await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(ticketHolder.Ticket.Id, taskId, subtaskId, SubtaskStatus.Complete, cancellationToken);
						if (subtaskUpdate != null)
						{
							ticketHolder.Update(subtaskUpdate);
						}

						await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Subtask completed: {summary}", cancellationToken);
						_logger.LogInformation("Subtask completed");
						subtaskComplete = true;
						break;
					}
					else
					{
						await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"QA rejected: {qaResult.Message}", cancellationToken);
						_logger.LogWarning("QA rejected, developer will retry: {Feedback}", qaResult.Message);

						_developerConversation.ResetIteration();
						await _developerConversation.AddUserMessageAsync($"QA rejected your work. Feedback: {qaResult.Message}\n\nPlease fix the issues and call end_subtask tool when done.", cancellationToken);
					}
				}
				else if (llmResult.ExitReason == LlmExitReason.Completed || llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
				{
					iterationCount++;

					_developerConversation.ResetIteration();

					if (iterationCount >= ContextResetThreshold)
					{
						await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Context reset: Developer exceeded iteration limit, starting fresh", cancellationToken);
						_logger.LogWarning("Context reset after {Count} iterations", iterationCount);

						await _developerConversation.FinalizeAsync(cancellationToken);
						string continuePrompt = $"""
							You were working on subtask '{subtaskName}' but got stuck. Look at the local changes and decide if you should continue or take a fresh approach.
							Description: {subtaskDescription}
							Call end_subtask tool when complete.
							""";
						ICompaction continueCompaction = CreateCompaction();

						ToolContext continueContext = new ToolContext(null, _currentTaskId, _currentSubtaskId, memories);
						_developerConversation = new LlmConversation(WorkerSession.Prompts["developer"], continuePrompt, memories, LlmRole.Developer, continueContext, continueCompaction, false, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-dev");
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
					await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Developer: Cost budget exceeded", cancellationToken);
					_logger.LogWarning("Developer exceeded cost budget");
					blocked = true;
					break;
				}
				else
				{
					await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Developer LLM failed: {llmResult.ErrorMessage}", cancellationToken);
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
		ToolContext qaContext = new ToolContext(null, _currentTaskId, _currentSubtaskId, memories);
		LlmConversation? qaConversation = new LlmConversation(WorkerSession.Prompts["qualityassurance"], userPrompt, memories, LlmRole.QA, qaContext, qaCompaction, false, "/workspace/logs", $"TIK-{WorkerSession.TicketHolder.Ticket.Id}-qa");

		try
		{
			for (;;)
			{
				LlmResult llmResult = await WorkerSession.LlmProxy.ContinueAsync(qaConversation, 8192, cancellationToken);

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
