using System.Collections.Concurrent;
using KanBeast.Shared;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services;

// Coordinates the persistent planning conversation that drives all work on a ticket.
// On startup, rebuilds all non-finalized conversations from the server. If the
// conversation needs LLM attention (pending user message, tool result, or unanswered
// tool calls from a crash), drives the LLM immediately before waiting for user input.
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

	// Creates or reconstitutes the planning conversation, then enters the main loop.
	// If the conversation needs LLM attention (pending user message, tool result, or
	// unanswered tool calls from a crash), drives the LLM before waiting for input.
	// Processes housekeeping (heartbeats, settings, ticket updates) while idle.
	public async Task RunAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		await EnsurePlanningConversationAsync(ticketHolder, cancellationToken);

		_logger.LogInformation("Planning agent ready, entering reactive loop");
		await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Ready, waiting for messages", cancellationToken);

		Console.WriteLine($"Orchestrator: Listening for chat on conversation {_planningConversation!.Id}");
		ConcurrentQueue<List<LLMConfig>> settingsQueue = WorkerSession.HubClient.GetSettingsQueue();
		ConcurrentQueue<Ticket> ticketQueue = WorkerSession.HubClient.GetTicketUpdateQueue();
		DateTimeOffset lastHeartbeat = DateTimeOffset.UtcNow;

		for (; ; )
		{
			cancellationToken.ThrowIfCancellationRequested();

			// The LlmService drains the chat queue, handles clear requests, and manages
			// interruptions internally. We just detect that work is pending and kick it off.
			if (NeedsLlmAttention(_planningConversation!) || WorkerSession.HubClient.HasPendingWork(_planningConversation!.Id))
			{
				await RunLlmUntilIdleAsync(ticketHolder, cancellationToken);
			}

			// Housekeeping while idle.
			DateTimeOffset now = DateTimeOffset.UtcNow;
			if ((now - lastHeartbeat).TotalSeconds >= 30)
			{
				lastHeartbeat = now;
				await WorkerSession.HubClient.SendHeartbeatAsync();
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
						WorkerSession.HubClient.GetChatQueue(_planningConversation.Id).Enqueue("Beast Mode Activated.  Call get_next_work_item and start the developer.");
					}
					else
					{
						_planningConversation.Role = LlmRole.Planning;
					}
				}
			}

			await Task.Delay(250, cancellationToken);
		}
	}

	// Rebuilds all non-finalized conversations from the server and identifies the
	// planning conversation. If none exists, creates a new one.
	private async Task EnsurePlanningConversationAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
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
		string planningRoleName = LlmRole.Planning.ToString();
		string planningActiveRoleName = LlmRole.PlanningActive.ToString();
		ConversationData? planningData = null;
		foreach (ConversationData data in nonFinalized)
		{
			if (data.Role == planningRoleName || data.Role == planningActiveRoleName)
			{
				planningData = data;
				break;
			}
		}

		LlmService? plannerService = WorkerSession.LlmProxy.GetService(ticketHolder.Ticket.PlannerLlmId!);
		if (plannerService==null)
			throw new Exception(ticketId + ": Planner LLM service not found for ID " + ticketHolder.Ticket.PlannerLlmId);

		if (planningData != null)
		{
			_logger.LogInformation("Reconstituting planning conversation: {Id}", planningData.Id);
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, "Planning: Reconstituted conversation from server", cancellationToken);

			ToolContext context = new ToolContext(null, null, plannerService, plannerService);
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

			ToolContext context = new ToolContext(null, null, plannerService, plannerService);
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

	// Returns true when the conversation has work the LLM should handle: a pending user
	// message, an unprocessed tool result, or unanswered tool calls from a crash.
	private static bool NeedsLlmAttention(ILlmConversation conversation)
	{
		List<ConversationMessage> messages = conversation.Messages;
		if (messages.Count == 0)
		{
			return false;
		}

		ConversationMessage last = messages[messages.Count - 1];
		if (last.Role == "user" || last.Role == "tool")
		{
			return true;
		}

		if (last.Role == "assistant" && last.ToolCalls != null && last.ToolCalls.Count > 0)
		{
			return true;
		}

		return false;
	}

	// Runs the planning LLM to completion. When active, wraps with BeginActiveWork/EndActiveWork
	// so the hub can cancel work if the ticket leaves Active status.
	private async Task RunLlmUntilIdleAsync(TicketHolder ticketHolder, CancellationToken cancellationToken)
	{
		LlmService service = _planningConversation!.ToolContext.LlmService!;

		bool isActive = ticketHolder.Ticket.Status == TicketStatus.Active;
		CancellationToken effectiveToken = cancellationToken;

		if (isActive)
		{
			effectiveToken = WorkerSession.HubClient.BeginActiveWork(cancellationToken);
			WorkerSession.UpdateCancellationToken(effectiveToken);
		}

		await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation!.Id, true);
		try
		{
			LlmResult result = await service.RunToCompletionAsync(_planningConversation!, null, true, effectiveToken);

			if (result.ExitReason == LlmExitReason.CostExceeded)
			{
				await WorkerSession.ApiClient.UpdateTicketStatusAsync(ticketHolder.Ticket.Id, "Failed", cancellationToken);
			}
		}
		catch (OperationCanceledException) when (isActive && !cancellationToken.IsCancellationRequested)
		{
			_logger.LogInformation("Planning: Work cancelled (ticket left Active)");
			await WorkerSession.ApiClient.AddActivityLogAsync(ticketHolder.Ticket.Id, "Planning: Work cancelled", CancellationToken.None);
		}
		finally
		{
			await WorkerSession.HubClient.SetConversationBusyAsync(_planningConversation!.Id, false);

			if (isActive)
			{
				WorkerSession.HubClient.EndActiveWork();
				WorkerSession.UpdateCancellationToken(cancellationToken);
			}
		}
	}
}
