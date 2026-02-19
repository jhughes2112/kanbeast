using System.Collections.Concurrent;
using KanBeast.Shared;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Coordinates the persistent planning conversation that drives all work on a ticket.
// On startup, rebuilds all non-finalized conversations from the server. If the planning
// conversation has pending tool calls (from a crash), replays them so work resumes.
public class AgentOrchestrator
{
	private readonly ILogger<AgentOrchestrator> _logger;
	private List<LLMConfig> _llmConfigs;

	private ILlmConversation? _planningConversation;

	public AgentOrchestrator(
		ILogger<AgentOrchestrator> logger,
		List<LLMConfig> llmConfigs)
	{
		_logger = logger;
		_llmConfigs = llmConfigs;
	}

	// Rebuilds all non-finalized conversations from the server and identifies the
	// planning conversation. If the planning conversation has pending tool calls,
	// replays them. If no planning conversation exists, creates a new one.
	public async Task EnsurePlanningConversationAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		if (_planningConversation != null)
		{
			return;
		}

		string ticketId = ticketHolder.Ticket.Id;

		// Fetch all non-finalized conversations so child agents can be found by ID during tool replay.
		List<ConversationData> nonFinalized = await WorkerSession.ApiClient.GetNonFinalizedConversationsAsync(ticketId, cancellationToken);
		_logger.LogInformation("Found {Count} non-finalized conversation(s) on server", nonFinalized.Count);

		// Find the planning conversation among them.
		ConversationData? planningData = null;
		foreach (ConversationData data in nonFinalized)
		{
			if (data.DisplayName == "Planning")
			{
				planningData = data;
				break;
			}
		}

		if (planningData != null)
		{
			_logger.LogInformation("Reconstituting planning conversation: {Id}", planningData.Id);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, "Planning: Reconstituted conversation from server", cancellationToken);

			ToolContext context = new ToolContext(null, null, null);
			_planningConversation = LlmConversationFactory.Reconstitute(planningData, LlmRole.Planning, context);

			await WorkerSession.HubClient.SyncConversationAsync(planningData);
		}
		else
		{
			_logger.LogInformation("Creating new planning conversation");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, "Planning: Creating new conversation", cancellationToken);

			string userPrompt = $"""
				Ticket: {ticketHolder.Ticket.Title}
				Description: {ticketHolder.Ticket.Description}
				""";

			ToolContext context = new ToolContext(null, null, null);
			_planningConversation = LlmConversationFactory.Create(
				LlmRole.Planning,
				context,
				userPrompt,
				"Planning",
				null);
		}

		// Sync the role to match current ticket status.
		if (ticketHolder.Ticket.Status == TicketStatus.Active)
		{
			_planningConversation.Role = LlmRole.PlanningActive;
		}

		await _planningConversation.ForceFlushAsync();
		_logger.LogInformation("Planning conversation ready: {Id}", _planningConversation.Id);
	}

	// Reactive loop: waits for chat messages from the client, appends them to the
	// planning conversation, calls the LLM, and syncs results back. Runs for the
	// lifetime of the worker process.
	//
	// On first entry, if the planning conversation has pending tool calls from a
	// prior crash, replays them before waiting for user input.
	public async Task RunReactiveLoopAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		_logger.LogInformation("Planning agent ready, waiting for messages");
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Ready, waiting for messages", cancellationToken);

		ConcurrentQueue<string> chatQueue = WorkerSession.HubClient.GetChatQueue(_planningConversation!.Id);
		Console.WriteLine($"Orchestrator: Listening for chat on conversation {_planningConversation!.Id}");
		ConcurrentQueue<string> clearQueue = WorkerSession.HubClient.GetClearQueue();
		ConcurrentQueue<List<LLMConfig>> settingsQueue = WorkerSession.HubClient.GetSettingsQueue();
		ConcurrentQueue<Ticket> ticketQueue = WorkerSession.HubClient.GetTicketUpdateQueue();
		DateTimeOffset lastHeartbeat = DateTimeOffset.UtcNow;

		// If the conversation has unanswered tool calls from a crash, replay them immediately.
		if (HasPendingToolCalls(_planningConversation))
		{
			_logger.LogInformation("Planning: Replaying pending tool calls from prior session");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Replaying pending tool calls", cancellationToken);

			bool isActive = ticketHolder.Ticket.Status == TicketStatus.Active;
			if (isActive)
			{
				CancellationToken activeToken = WorkerSession.HubClient.BeginActiveWork(cancellationToken);
				WorkerSession.UpdateCancellationToken(activeToken);
				try
				{
					await RunLlmUntilIdleAsync(ticketHolder, activeToken);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					_logger.LogInformation("Planning: Replay cancelled (ticket left Active)");
				}
				finally
				{
					WorkerSession.HubClient.EndActiveWork();
					WorkerSession.UpdateCancellationToken(cancellationToken);
				}
			}
			else
			{
				await RunLlmUntilIdleAsync(ticketHolder, cancellationToken);
			}

			await _planningConversation.ForceFlushAsync();
		}

		for (; ; )
		{
			cancellationToken.ThrowIfCancellationRequested();

			string? message = null;
			while (!chatQueue.TryDequeue(out message))
			{
				cancellationToken.ThrowIfCancellationRequested();

				DateTimeOffset now = DateTimeOffset.UtcNow;
				if ((now - lastHeartbeat).TotalSeconds >= 30)
				{
					lastHeartbeat = now;
					await WorkerSession.HubClient.SendHeartbeatAsync();
				}

				while (clearQueue.TryDequeue(out string? clearConversationId))
				{
					if (clearConversationId == _planningConversation!.Id)
					{
						_logger.LogInformation("Planning: Clearing conversation");
						await _planningConversation.ResetAsync();
					}
				}

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

				Ticket? latestTicket = null;
				while (ticketQueue.TryDequeue(out Ticket? updated))
				{
					latestTicket = updated;
				}
				if (latestTicket != null)
				{
					TicketStatus previousStatus = ticketHolder.Ticket.Status;
					ticketHolder.Update(latestTicket);

					if (previousStatus != latestTicket.Status && _planningConversation != null)
					{
						if (latestTicket.Status == TicketStatus.Active)
						{
							_planningConversation.Role = LlmRole.PlanningActive;
							chatQueue.Enqueue("Beast Mode Activated.  Call get_next_work_item and start the developer.");
						}
						else
						{
							_planningConversation.Role = LlmRole.Planning;
						}
					}
				}

				await Task.Delay(250, cancellationToken);
			}

			_logger.LogInformation("Planning: Received chat message from user");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Received user message", cancellationToken);

			await _planningConversation!.AddUserMessageAsync(message, cancellationToken);

			bool isActiveNow = ticketHolder.Ticket.Status == TicketStatus.Active;

			if (isActiveNow)
			{
				CancellationToken activeToken = WorkerSession.HubClient.BeginActiveWork(cancellationToken);
				WorkerSession.UpdateCancellationToken(activeToken);
				try
				{
					await RunLlmUntilIdleAsync(ticketHolder, activeToken);
				}
				catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
				{
					_logger.LogInformation("Planning: Work cancelled (ticket left Active)");
					await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Work cancelled", CancellationToken.None);
				}
				finally
				{
					WorkerSession.HubClient.EndActiveWork();
					WorkerSession.UpdateCancellationToken(cancellationToken);
				}
			}
			else
			{
				await RunLlmUntilIdleAsync(ticketHolder, cancellationToken);
			}

			await _planningConversation.ForceFlushAsync();
		}
	}

	// Returns true if the last assistant message has tool calls with no corresponding tool result messages after it.
	private static bool HasPendingToolCalls(ILlmConversation conversation)
	{
		List<ConversationMessage> messages = conversation.Messages;
		if (messages.Count == 0)
		{
			return false;
		}

		ConversationMessage last = messages[messages.Count - 1];
		if (last.Role == "assistant" && last.ToolCalls != null && last.ToolCalls.Count > 0)
		{
			return true;
		}

		return false;
	}

	// Calls the LLM in a loop until it responds with text (idle), is interrupted,
	// or hits a fatal/cost error. Tool calls execute concurrently as Tasks.
	// Between iterations, drains chat, interrupt, ticket, and settings queues.
	private async Task RunLlmUntilIdleAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		int retryCount = 0;
		ConcurrentQueue<string> interruptQueue = WorkerSession.HubClient.GetInterruptQueue();
		ConcurrentQueue<string> chatQueue = WorkerSession.HubClient.GetChatQueue(_planningConversation!.Id);
		ConcurrentQueue<Ticket> ticketQueue = WorkerSession.HubClient.GetTicketUpdateQueue();
		ConcurrentQueue<List<LLMConfig>> settingsQueue = WorkerSession.HubClient.GetSettingsQueue();

		await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation!.Id, true);
		try
		{
			for (; ; )
			{
				cancellationToken.ThrowIfCancellationRequested();

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

				while (chatQueue.TryDequeue(out string? userMessage))
				{
					_logger.LogInformation("Planning: Appending user message received mid-loop");
					await _planningConversation!.AddUserMessageAsync(userMessage, cancellationToken);
				}

				Ticket? latestTicket = null;
				while (ticketQueue.TryDequeue(out Ticket? updated))
				{
					latestTicket = updated;
				}
				if (latestTicket != null)
				{
					ticketHolder.Update(latestTicket);
				}

				List<LLMConfig>? latestConfigs = null;
				while (settingsQueue.TryDequeue(out List<LLMConfig>? configs))
				{
					latestConfigs = configs;
				}
				if (latestConfigs != null && latestConfigs.Count > 0)
				{
					_logger.LogInformation("Applying updated LLM configs mid-loop ({Count} endpoint(s))", latestConfigs.Count);
					WorkerSession.LlmProxy.UpdateConfigs(latestConfigs);
					_llmConfigs = latestConfigs;
				}

				string? plannerLlmId = ticketHolder.Ticket.PlannerLlmId;
				LlmResult llmResult = !string.IsNullOrWhiteSpace(plannerLlmId)
					? await WorkerSession.LlmProxy.ContinueWithConfigIdAsync(plannerLlmId, _planningConversation!, 16384, cancellationToken)
					: await WorkerSession.LlmProxy.ContinueAsync(_planningConversation!, 16384, cancellationToken);
				_logger.LogDebug("Planning response: {Response}", llmResult.Content);

				if (llmResult.ExitReason == LlmExitReason.Completed)
				{
					return;
				}
				else if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit)
				{
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

					await _planningConversation.AddUserMessageAsync("Continue working.", cancellationToken);
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
		finally
		{
			await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation!.Id, false);
		}
	}
}
