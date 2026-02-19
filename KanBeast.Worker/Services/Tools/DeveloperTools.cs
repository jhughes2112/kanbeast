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
		- Choose the best LLM for the work based on its strengths/weaknesses and cost. Pass the developer model as llmConfigId and a cheaper model for its sub-agents as subAgentLlmConfigId.
		- The developer will work autonomously on the subtask and return a summary of what was accomplished, including a brief evaluation of each sub-agent's performance.
		- You receive the developer's final report and can decide whether to move on or re-try the subtask with a different LLM.
		- Use the sub-agent evaluation in the report to update_llm_notes for both the developer and sub-agent models.

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
		[Description("The LLM config id to use for sub-agents spawned by this developer. Use a cheaper model for research and file searches.")] string subAgentLlmConfigId,
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

				LlmService? subAgentService = WorkerSession.LlmProxy.GetService(subAgentLlmConfigId);
				if (subAgentService == null)
				{
					result = new ToolResult($"Error: Sub-agent LLM config '{subAgentLlmConfigId}' not found", false, false);
					return result;
				}

				ToolContext devContext = new ToolContext(taskId, subtaskId, service, subAgentService);

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

				LlmResult llmResult = await service.RunToCompletionAsync(conversation, "Continue working or call end_subtask tool when done.", false, context.CancellationToken);

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
		sb.AppendLine("# Sub-agent evaluation");
		sb.AppendLine("If you use sub-agents, include a brief evaluation of each one's performance in your end_subtask summary (25 words max). Note what it did well and what it struggled with.");

		return sb.ToString();
	}
}
