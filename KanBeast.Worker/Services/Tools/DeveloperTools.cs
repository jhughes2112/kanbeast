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
		[Description("The name of the subtask to implement, or the task name if there are no subtasks")] string subtaskName,
		[Description("Full description and acceptance criteria for the subtask or task")] string subtaskDescription,
		[Description("The task ID from the ticket")] string taskId,
		[Description("The subtask ID from the ticket, or empty string if the task has no subtasks")] string subtaskId,
		[Description("The LLM config id to use for this developer session")] string id,
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
				bool hasSubtask = !string.IsNullOrWhiteSpace(subtaskId);
				string ticketId = WorkerSession.TicketHolder.Ticket.Id;

				// Mark the work item as in-progress.
				if (hasSubtask)
				{
					await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(ticketId, taskId, subtaskId, SubtaskStatus.InProgress, WorkerSession.CancellationToken);
				}
				else
				{
					await WorkerSession.ApiClient.UpdateTaskStatusAsync(ticketId, taskId, SubtaskStatus.InProgress, WorkerSession.CancellationToken);
				}

				string initialPrompt = BuildDeveloperPrompt(taskName, subtaskName, subtaskDescription);

				LlmService? service = WorkerSession.LlmProxy.GetService(id);
				if (service == null)
				{
					result = new ToolResult($"Error: LLM config '{id}' not found", false, false);
					return result;
				}

				string modelTag = $"{id[..4]}:{service.Model}";
				await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, $"Started work [{modelTag}]: {subtaskName}", WorkerSession.CancellationToken);

				// Check for a prior conversation from a crashed run.
				ILlmConversation conversation;
				string? toolCallId = ToolContext.ActiveToolCallId;

				ConversationData? existing = toolCallId != null
					? await WorkerSession.ApiClient.GetConversationAsync(ticketId, toolCallId, WorkerSession.CancellationToken)
					: null;

				if (existing != null)
				{
					conversation = new CompactingConversation(existing, LlmRole.Developer, service, null, null, null, null, taskId, subtaskId);
					Console.WriteLine($"[Developer] Reconstituted conversation {toolCallId}");
				}
				else
				{
					conversation = new CompactingConversation(null, LlmRole.Developer, service, null, initialPrompt, $"Developer - {subtaskName}", toolCallId, taskId, subtaskId);
				}

				bool workComplete = false;

				LlmResult llmResult;
				await WorkerSession.HubClient.SetConversationBusyAsync(conversation.Id, true);
				try
				{
					llmResult = await service.RunToCompletionAsync(conversation, "Are you done? If so, call end_subtask to report the task status ({messagesRemaining} turns remaining).", false, true, context.CancellationToken);
				}
				finally
				{
					await WorkerSession.HubClient.SetConversationBusyAsync(conversation.Id, false);
				}

				if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "end_subtask")
				{
					Ticket? statusUpdate;

					if (hasSubtask)
					{
						statusUpdate = await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(ticketId, taskId, subtaskId, SubtaskStatus.Complete, WorkerSession.CancellationToken);
					}
					else
					{
						statusUpdate = await WorkerSession.ApiClient.UpdateTaskStatusAsync(ticketId, taskId, SubtaskStatus.Complete, WorkerSession.CancellationToken);
					}

					if (statusUpdate != null)
					{
						WorkerSession.TicketHolder.Update(statusUpdate);
					}

					await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, $"Work completed: {llmResult.Content}", WorkerSession.CancellationToken);
					workComplete = true;
				}

				if (workComplete)
				{
					result = new ToolResult($"Developer completed '{subtaskName}': {llmResult.Content}", false, false);
				}
				else
				{
					result = new ToolResult($"Developer could not complete '{subtaskName}': {llmResult.Content}", false, false);
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

	// Builds the user prompt scoped to just the assigned task and subtask.
	private static string BuildDeveloperPrompt(string taskName, string subtaskName, string subtaskDescription)
	{
		StringBuilder sb = new StringBuilder();
		sb.AppendLine($"# Task: {taskName}");
		sb.AppendLine();
		sb.AppendLine($"# Your Assignment: {subtaskName}");
		sb.AppendLine();
		sb.AppendLine(subtaskDescription);
		sb.AppendLine();
		sb.AppendLine("Call end_subtask tool when complete.");
		sb.AppendLine("Use list_available_llms to see which models you can pass to start_sub_agent.");

		return sb.ToString();
	}
}
