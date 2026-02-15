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
	private List<LLMConfig> _llmConfigs;

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

			// Always patch message 0 with the latest system prompt so prompt changes take effect.
			if (existing.Messages.Count > 0)
			{
				existing.Messages[0] = new ConversationMessage { Role = "system", Content = WorkerSession.Prompts["planning"] };
			}

			ICompaction compaction = CreateCompaction();
			ConversationMemories memories = new ConversationMemories(existing.Memories);
			ToolContext context = new ToolContext(null, null, memories);
			_planningConversation = new LlmConversation(existing, LlmRole.Planning, context, compaction);
			context.OnMemoriesChanged = _planningConversation.RefreshMemoriesMessage;

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
			ToolContext context = new ToolContext(null, null, memories);
			_planningConversation = new LlmConversation(
				WorkerSession.Prompts["planning"],
				userPrompt,
				memories,
				LlmRole.Planning,
				context,
				compaction,
				"Planning");
			context.OnMemoriesChanged = _planningConversation.RefreshMemoriesMessage;
		}

		// Sync immediately so the client can see the conversation in the dropdown.
		_logger.LogInformation("Syncing planning conversation to server: {Id}", _planningConversation.Id);
		await _planningConversation.ForceFlushAsync();
		_logger.LogInformation("Planning conversation synced successfully");
	}

	// Reactive loop: waits for chat messages from the client, appends them to the
	// planning conversation, calls the LLM, and syncs results back. Runs for the
	// lifetime of the worker process.
	public async Task RunReactiveLoopAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Planning agent ready, waiting for messages");
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Ready, waiting for messages", cancellationToken);

		System.Collections.Concurrent.ConcurrentQueue<string> chatQueue = WorkerSession.HubClient.GetChatQueue(_planningConversation!.Id);
		Console.WriteLine($"Orchestrator: Listening for chat on conversation {_planningConversation!.Id}");
		System.Collections.Concurrent.ConcurrentQueue<string> clearQueue = WorkerSession.HubClient.GetClearQueue();
		System.Collections.Concurrent.ConcurrentQueue<List<LLMConfig>> settingsQueue = WorkerSession.HubClient.GetSettingsQueue();
		System.Collections.Concurrent.ConcurrentQueue<Ticket> ticketQueue = WorkerSession.HubClient.GetTicketUpdateQueue();

		for (;;)
		{
			cancellationToken.ThrowIfCancellationRequested();

			string? message = null;
			while (!chatQueue.TryDequeue(out message))
			{
				cancellationToken.ThrowIfCancellationRequested();

				// Drain any pending clear requests while idle.
				while (clearQueue.TryDequeue(out string? clearConversationId))
				{
					if (clearConversationId == _planningConversation!.Id)
					{
						_logger.LogInformation("Planning: Clearing conversation");
						await _planningConversation.ResetAsync();
					}
				}

				// Apply any pending LLM config updates (keep only the latest).
				List<LLMConfig>? latestConfigs = null;
				while (settingsQueue.TryDequeue(out List<LLMConfig>? configs))
				{
					latestConfigs = configs;
				}
				if (latestConfigs != null && latestConfigs.Count > 0)
				{
					_logger.LogInformation("Applying updated LLM configs ({Count} endpoint(s))", latestConfigs.Count);
					WorkerSession.LlmProxy.UpdateConfigs(latestConfigs);
					_llmConfigs = latestConfigs;
				}

				// Apply any pending ticket updates (keep only the latest).
				Ticket? latestTicket = null;
				while (ticketQueue.TryDequeue(out Ticket? updated))
				{
					latestTicket = updated;
				}
				if (latestTicket != null)
				{
					ticketHolder.Update(latestTicket);
				}

				await Task.Delay(250, cancellationToken);
			}

			_logger.LogInformation("Planning: Received chat message from user");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Received user message", cancellationToken);

			await _planningConversation!.AddUserMessageAsync(message, cancellationToken);

			await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation.Id, true);
			try
			{
				await RunLlmUntilIdleAsync(ticketHolder, cancellationToken);
			}
			finally
			{
				await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation.Id, false);
			}

			await _planningConversation.ForceFlushAsync();
		}
	}

	// Calls the LLM in a loop until it responds with text (idle), is interrupted,
	// or hits a fatal/cost error. Tool calls execute inline.
	private async Task RunLlmUntilIdleAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		int retryCount = 0;
		System.Collections.Concurrent.ConcurrentQueue<string> interruptQueue = WorkerSession.HubClient.GetInterruptQueue();

		for (;;)
		{
			cancellationToken.ThrowIfCancellationRequested();

			// Check for interrupt requests between LLM calls.
			bool interrupted = false;
			while (interruptQueue.TryDequeue(out string? interruptConvId))
			{
				if (interruptConvId == _planningConversation!.Id)
				{
					interrupted = true;
				}
			}
			if (interrupted)
			{
				_logger.LogInformation("Planning: Interrupted by user");
				await _planningConversation!.AddUserMessageAsync("[System: The user interrupted the current operation. Stop what you are doing and respond to the user.]", cancellationToken);
				return;
			}

			string? plannerLlmId = ticketHolder.Ticket.PlannerLlmId;
			LlmResult llmResult = !string.IsNullOrWhiteSpace(plannerLlmId)
				? await WorkerSession.LlmProxy.ContinueWithConfigIdAsync(plannerLlmId, _planningConversation!, 16384, cancellationToken)
				: await WorkerSession.LlmProxy.ContinueAsync(_planningConversation!, 16384, cancellationToken);
			_logger.LogDebug("Planning response: {Response}", llmResult.Content);

			if (llmResult.ExitReason == LlmExitReason.Completed)
			{
				// LLM responded with text and no tool calls. Idle until next user message.
				return;
			}
			else if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit)
			{
				// A tool signalled exit. Sync and continue.
				await _planningConversation!.ForceFlushAsync();
				retryCount = 0;
			}
			else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				retryCount++;

				if (retryCount >= 5)
				{
					_logger.LogWarning("Planning hit iteration limit {Count} times, pausing", retryCount);
					await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Paused - exceeded retry limit", cancellationToken);
					return;
				}

				_logger.LogWarning("Planning hit iteration limit, prompting to continue");
				_planningConversation!.ResetIteration();

				await _planningConversation.AddUserMessageAsync(
					"Continue working.",
					cancellationToken);
			}
			else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
			{
				_logger.LogWarning("Planning exceeded cost budget");
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Cost budget exceeded", cancellationToken);
				await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed", cancellationToken);
				return;
			}
			else
			{
				_logger.LogError("Planning LLM failed: {Error}", llmResult.ErrorMessage);
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning LLM failed: {llmResult.ErrorMessage}", cancellationToken);
				return;
			}
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
}
