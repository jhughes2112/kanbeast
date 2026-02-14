using KanBeast.Shared;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Coordinates the persistent planning conversation that drives all work on a ticket.
// The planning agent creates tasks/subtasks, then uses start_developer to spawn developer
// conversations for each subtask. The planning conversation lives for the entire lifetime
// of the worker process and is never finalized, so the user can always chat with it.
public class AgentOrchestrator
{
	private readonly ILogger<AgentOrchestrator> _logger;
	private readonly CompactionSettings _compactionSettings;
	private readonly List<LLMConfig> _llmConfigs;

	private LlmConversation? _planningConversation;

	public AgentOrchestrator(
		ILogger<AgentOrchestrator> logger,
		CompactionSettings compactionSettings,
		List<LLMConfig> llmConfigs)
	{
		_logger = logger;
		_compactionSettings = compactionSettings;
		_llmConfigs = llmConfigs;
	}

	// Ensures the planning conversation exists, either by reconstituting from the server
	// or creating a fresh one. Called once before the first Active cycle.
	public async Task EnsurePlanningConversationAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		if (_planningConversation != null)
		{
			return;
		}

		// Try to reconstitute from server.
		ConversationData? existing = await WorkerSession.ApiClient.GetPlanningConversationAsync(ticketHolder.Ticket.Id, cancellationToken);

		if (existing != null)
		{
			_logger.LogInformation("Reconstituting planning conversation from server: {Id}", existing.Id);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Reconstituted conversation from server", cancellationToken);

			ICompaction compaction = CreateCompaction();
			ConversationMemories memories = new ConversationMemories(existing.Memories);
			ToolContext context = new ToolContext(null, null, null, memories);
			_planningConversation = new LlmConversation(existing, LlmRole.Planning, context, compaction, "/workspace/logs", $"TIK-{ticketHolder.Ticket.Id}-plan");

			// Force a sync so clients get a ConversationsUpdated notification.
			await WorkerSession.HubClient.SyncConversationAsync(existing);
		}
		else
		{
			_logger.LogInformation("Creating new planning conversation");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Creating new conversation", cancellationToken);

			ConversationMemories memories = new ConversationMemories();

			string userPrompt = $"""
				Ticket: {ticketHolder.Ticket.Title}
				Description: {ticketHolder.Ticket.Description}
				""";

			ICompaction compaction = CreateCompaction();
			ToolContext context = new ToolContext(null, null, null, memories);
			_planningConversation = new LlmConversation(
				WorkerSession.Prompts["planning"],
				userPrompt,
				memories,
				LlmRole.Planning,
				context,
				compaction,
				"/workspace/logs",
				$"TIK-{ticketHolder.Ticket.Id}-plan",
				"Planning");
		}

		// Sync immediately so the client can see the conversation in the dropdown.
		_logger.LogInformation("Syncing planning conversation to server: {Id}", _planningConversation.Id);
		await _planningConversation.SyncToServerAsync();
		_logger.LogInformation("Planning conversation synced successfully");
	}

	// Runs one Active cycle. The planning conversation is reused across cycles.
	public async Task StartAgents(TicketHolder ticketHolder, string workDir, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Orchestrator starting for ticket: {Title}", ticketHolder.Ticket.Title);
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Orchestrator: Starting work", cancellationToken);

		bool result = await RunPlanningLoopAsync(ticketHolder, cancellationToken);

		// Sync the conversation after work ends so the latest state is on the server.
		await _planningConversation!.SyncToServerAsync();

		if (result)
		{
			decimal finalCost = ticketHolder.Ticket.LlmCost;
			_logger.LogInformation("Orchestrator completed successfully (spent ${Spend:F4})", finalCost);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Completed successfully (spent ${finalCost:F4})", cancellationToken);
			await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Done", cancellationToken);
		}
		else
		{
			decimal currentCost = ticketHolder.Ticket.LlmCost;
			_logger.LogWarning("Orchestrator blocked (spent ${Spend:F4})", currentCost);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Orchestrator: Blocked (spent ${currentCost:F4})", cancellationToken);
			await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed", cancellationToken);
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

	// Runs the planning conversation in a loop. The planning agent creates a plan, then
	// calls start_developer for each subtask. The conversation continues until it signals
	// planning_complete (after all development work), or a fatal error occurs.
	private async Task<bool> RunPlanningLoopAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Planning: Continuing conversation");
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Continuing conversation", cancellationToken);

		int retryCount = 0;

		for (;;)
		{
			LlmResult llmResult = await WorkerSession.LlmProxy.ContinueAsync(_planningConversation!, 16384, cancellationToken);
			_logger.LogDebug("Planning response: {Response}", llmResult.Content);

			if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "planning_complete")
			{
				int subtaskCount = 0;
				foreach (KanbanTask task in ticketHolder.Ticket.Tasks)
				{
					subtaskCount += task.Subtasks.Count;
				}
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning complete.", cancellationToken);
				_logger.LogInformation("Planning complete: {Count} subtasks", subtaskCount);
				return true;
			}
			else if (llmResult.ExitReason == LlmExitReason.Completed)
			{
				retryCount++;

				if (retryCount >= 5)
				{
					_logger.LogWarning("Planning failed to call tool after {Count} attempts, aborting", retryCount);
					await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Aborted - LLM failed to call tools after multiple attempts", cancellationToken);
					return false;
				}

				await _planningConversation!.AddUserMessageAsync(
					"Continue working. Create tasks and subtasks if planning is not done. Use start_developer to implement subtasks. Call planning_complete when all work is finished.",
					cancellationToken);
			}
			else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				retryCount++;

				if (retryCount >= 5)
				{
					_logger.LogWarning("Planning hit iteration limit {Count} times, aborting", retryCount);
					await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Aborted - exceeded retry limit", cancellationToken);
					return false;
				}

				_logger.LogWarning("Planning hit iteration limit, prompting to continue");
				_planningConversation!.ResetIteration();

				await _planningConversation.AddUserMessageAsync(
					"Are you making good progress? Keep working. Use start_developer to implement subtasks. Call planning_complete when everything is done.",
					cancellationToken);
			}
			else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
			{
				_logger.LogWarning("Planning exceeded cost budget");
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Cost budget exceeded", cancellationToken);
				return false;
			}
			else
			{
				_logger.LogError("Planning LLM failed: {Error}", llmResult.ErrorMessage);
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning LLM failed: {llmResult.ErrorMessage}", cancellationToken);
				return false;
			}
		}
	}
}
