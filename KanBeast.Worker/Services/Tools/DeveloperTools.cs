using System.ComponentModel;
using System.Text;
using KanBeast.Shared;

namespace KanBeast.Worker.Services.Tools;

// Tool that spawns a developer LLM conversation to implement a subtask.
// Only available to the planning agent.
public static class DeveloperTools
{
	[Description("""
		Launch a developer agent to implement a specific subtask from the plan.
		The developer has full capabilities: shell, files, search, web, and the ability to launch sub-agents.

		When to use:
		- After planning is complete and the ticket status is Active, call get_next_work_item first to find the next subtask and available LLMs.
		- Choose the best LLM for the developer based on its strengths/weaknesses and cost. Pass the developer model as llmConfigId.
		- The developer will choose its own sub-agent LLM from the available models.
		- The developer will work autonomously on the subtask and return a summary of what was accomplished.
		- You receive the developer's final report and can decide whether to move on or re-try the subtask with a different LLM.

		Usage notes:
		1. Provide the task name, subtask name, and full description so the developer has complete context.
		2. Each invocation is a full developer session — the developer works until it calls end_subtask.
		3. The developer can read files, write code, run commands, search, and launch sub-agents to assist.
		4. After the developer returns, review its report and decide the next step.
		5. If the developer fails, call get_next_work_item again to try with a different LLM.
		""")]
	public static async Task<ToolResult> StartDeveloperAsync(
		[Description("The name of the parent task")] string taskName,
		[Description("The name of the subtask to implement")] string subtaskName,
		[Description("Full description and acceptance criteria for the subtask")] string subtaskDescription,
		[Description("The task ID from the ticket")] string taskId,
		[Description("The subtask ID from the ticket")] string subtaskId,
		[Description("The LLM config id to use for this developer session, from get_next_work_item")] string llmConfigId,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(subtaskName) || string.IsNullOrWhiteSpace(subtaskDescription))
		{
			result = new ToolResult("Error: subtaskName and subtaskDescription are required", false, false);
		}
		else
		{
			try
			{
				await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(WorkerSession.TicketHolder.Ticket.Id, taskId, subtaskId, SubtaskStatus.InProgress, WorkerSession.CancellationToken);
				await WorkerSession.ApiClient.AddActivityLogAsync(WorkerSession.TicketHolder.Ticket.Id, $"Started subtask: {subtaskName}", WorkerSession.CancellationToken);

				string initialPrompt = BuildDeveloperPrompt(taskName, subtaskName, subtaskDescription);

				LlmService? service = WorkerSession.LlmProxy.GetService(llmConfigId);
				if (service == null)
				{
					result = new ToolResult($"Error: LLM config '{llmConfigId}' not found", false, false);
					return result;
				}

				ToolContext devContext = new ToolContext(taskId, subtaskId, service, null);

				string ticketId = WorkerSession.TicketHolder.Ticket.Id;

				// Check for a prior conversation from a crashed run.
				ILlmConversation conversation;
				string? toolCallId = ToolContext.ActiveToolCallId;

				ConversationData? existing = toolCallId != null
					? await WorkerSession.ApiClient.GetConversationAsync(ticketId, toolCallId, WorkerSession.CancellationToken)
					: null;

				if (existing != null)
				{
					conversation = new CompactingConversation(existing, LlmRole.Developer, devContext, null, null, null);
					Console.WriteLine($"[Developer] Reconstituted conversation {toolCallId}");
				}
				else
				{
					conversation = new CompactingConversation(null, LlmRole.Developer, devContext, initialPrompt, $"Developer - {subtaskName}", toolCallId);
				}

				bool subtaskComplete = false;

				LlmResult llmResult;
				await WorkerSession.HubClient.SetConversationBusyAsync(conversation.Id, true);
				try
				{
					llmResult = await service.RunToCompletionAsync(conversation, "Continue working. Call end_subtask when done ({messagesRemaining} turns remaining).", false, true, context.CancellationToken);
				}
				finally
				{
					await WorkerSession.HubClient.SetConversationBusyAsync(conversation.Id, false);
				}

				if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "end_subtask")
				{
					Ticket? subtaskUpdate = await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(ticketId, taskId, subtaskId, SubtaskStatus.Complete, WorkerSession.CancellationToken);

					if (subtaskUpdate != null)
					{
						WorkerSession.TicketHolder.Update(subtaskUpdate);
					}

					await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, $"Subtask completed: {llmResult.Content}", WorkerSession.CancellationToken);
					subtaskComplete = true;
				}

				if (subtaskComplete)
				{
					result = new ToolResult($"Developer completed subtask '{subtaskName}': {llmResult.Content}", false, false);
				}
				else
				{
					result = new ToolResult($"Developer could not complete subtask '{subtaskName}': {llmResult.Content}", false, false);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Developer failed: {ex.Message}", false, false);
			}
		}

		return result;
	}

	// Builds the user prompt for a developer conversation with full ticket context.
	private static string BuildDeveloperPrompt(string taskName, string subtaskName, string subtaskDescription)
	{
		Ticket ticket = WorkerSession.TicketHolder.Ticket;

		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"# Ticket: {ticket.Title}");
		sb.AppendLine();
		sb.AppendLine(ticket.Description);
		sb.AppendLine();

		sb.AppendLine("# Task Overview");
		foreach (KanbanTask task in ticket.Tasks)
		{
			sb.AppendLine($"- {task.Name}");
			foreach (KanbanSubtask subtask in task.Subtasks)
			{
				string marker = subtask.Status == SubtaskStatus.Complete ? "✓" : " ";
				sb.AppendLine($"  [{marker}] {subtask.Name}");
			}
		}
		sb.AppendLine();

		sb.AppendLine($"# Your Assignment: {subtaskName} (in task '{taskName}')");
		sb.AppendLine();
		sb.AppendLine(subtaskDescription);
		sb.AppendLine();
		sb.AppendLine("Call end_subtask tool when complete.");
		sb.AppendLine();

		// Include available LLMs so the developer can pick its own sub-agent model.
		decimal remainingBudget = ticket.MaxCost <= 0 ? 0m : Math.Max(0m, ticket.MaxCost - ticket.LlmCost);
		List<(string id, string model, string strengths, string weaknesses, decimal costPer1MTokens, bool isAvailable)> llms =
			WorkerSession.LlmProxy.GetAvailableLlmSummaries(remainingBudget);

		sb.AppendLine("# Available LLMs for sub-agents");
		sb.AppendLine("Choose a sub-agent LLM from this list when launching start_sub_agent. Prefer cheaper models for straightforward tool work.");
		foreach ((string id, string model, string strengths, string weaknesses, decimal costPer1MTokens, bool isAvailable) llm in llms)
		{
			if (!llm.isAvailable)
			{
				continue;
			}

			sb.Append($"  - id: {llm.id}, model: {llm.model}, cost: ${llm.costPer1MTokens:F2}");
			if (!string.IsNullOrWhiteSpace(llm.strengths))
			{
				sb.Append($", strengths: {llm.strengths}");
			}
			if (!string.IsNullOrWhiteSpace(llm.weaknesses))
			{
				sb.Append($", weaknesses: {llm.weaknesses}");
			}
			sb.AppendLine();
		}

		return sb.ToString();
	}
}
