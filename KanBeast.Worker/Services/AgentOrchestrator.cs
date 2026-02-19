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

		LlmService? plannerService = !string.IsNullOrWhiteSpace(ticketHolder.Ticket.PlannerLlmId)
			? WorkerSession.LlmProxy.GetService(ticketHolder.Ticket.PlannerLlmId)
			: null;

		if (planningData != null)
		{
			_logger.LogInformation("Reconstituting planning conversation: {Id}", planningData.Id);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, "Planning: Reconstituted conversation from server", cancellationToken);

			ToolContext context = new ToolContext(null, null, plannerService, null);
			_planningConversation = new CompactingConversation(planningData, LlmRole.Planning, context, null, null, null);

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

			ToolContext context = new ToolContext(null, null, plannerService, null);
			_planningConversation = new CompactingConversation(null, LlmRole.Planning, context, userPrompt, "Planning", null);
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

	// Delegates to LlmService.RunToCompletionAsync. Interrupts and chat messages are
	// handled internally by the LlmService loop via per-conversation CTS.
	private async Task RunLlmUntilIdleAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		LlmService? service = _planningConversation!.ToolContext.LlmService;
		if (service == null)
		{
			_logger.LogError("Planning cannot continue: no LLM service configured");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: No LLM configured for planner", cancellationToken);
			return;
		}

		await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation!.Id, true);
		try
		{
			LlmResult result = await service.RunToCompletionAsync(_planningConversation!, null, true, cancellationToken);

			if (result.ExitReason == LlmExitReason.CostExceeded)
			{
				_logger.LogWarning("Planning exceeded cost budget");
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Cost budget exceeded", cancellationToken);
				await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed", cancellationToken);
			}
			else if (result.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				_logger.LogWarning("Planning hit iteration limit, pausing");
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Paused - exceeded retry limit", cancellationToken);
			}
			else if (result.ExitReason == LlmExitReason.Interrupted)
			{
				_logger.LogInformation("Planning: Interrupted by user");
			}
			else if (result.ExitReason != LlmExitReason.Completed && !string.IsNullOrWhiteSpace(result.ErrorMessage))
			{
				_logger.LogError("Planning LLM failed: {Error}", result.ErrorMessage);
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, $"Planning LLM failed: {result.ErrorMessage}", cancellationToken);
			}
		}
		finally
		{
			await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation!.Id, false);
		}
	}
}
