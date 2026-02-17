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
			result = new ToolResult("Error: subtaskName and subtaskDescription are required", false);
		}
		else
		{
			try
			{
				await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(
					WorkerSession.TicketHolder.Ticket.Id, taskId, subtaskId,
					SubtaskStatus.InProgress, WorkerSession.CancellationToken);

				await WorkerSession.ApiClient.AddActivityLogAsync(
						WorkerSession.TicketHolder.Ticket.Id,
						$"Started subtask: {subtaskName}",
						WorkerSession.CancellationToken);

					string initialPrompt = BuildDeveloperPrompt(taskName, subtaskName, subtaskDescription);

				string systemPrompt = WorkerSession.Prompts.TryGetValue("developer", out string? devPrompt) ? devPrompt : string.Empty;
				ConversationMemories memories = new ConversationMemories(context.Memories);
				ICompaction compaction = new CompactionSummarizer(
					WorkerSession.Prompts.TryGetValue("compaction", out string? compPrompt) ? compPrompt : string.Empty,
					WorkerSession.LlmProxy,
					0.9);

				string? resolvedSubAgentId = string.IsNullOrWhiteSpace(subAgentLlmConfigId) ? null : subAgentLlmConfigId;
				ToolContext devContext = new ToolContext(taskId, subtaskId, memories, resolvedSubAgentId, null);

				string ticketId = WorkerSession.TicketHolder.Ticket.Id;
				ILlmConversation conversation = new CompactingConversation(
					systemPrompt,
					initialPrompt,
					memories,
					LlmRole.Developer,
					devContext,
					compaction,
					$"Developer - {subtaskName}");
				devContext.OnMemoriesChanged = conversation.RefreshMemoriesMessage;

				string content = string.Empty;
				int iterationCount = 0;
				int contextResetCount = 0;
				bool subtaskComplete = false;

				for (;;)
				{
					LlmResult llmResult = !string.IsNullOrWhiteSpace(llmConfigId)
						? await WorkerSession.LlmProxy.ContinueWithConfigIdAsync(llmConfigId, conversation, null, WorkerSession.CancellationToken)
						: await WorkerSession.LlmProxy.ContinueAsync(conversation, null, WorkerSession.CancellationToken);

					if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit && llmResult.FinalToolCalled == "end_subtask")
					{
						content = llmResult.Content;

						Ticket? subtaskUpdate = await WorkerSession.ApiClient.UpdateSubtaskStatusAsync(
							ticketId, taskId, subtaskId,
							SubtaskStatus.Complete, WorkerSession.CancellationToken);

						if (subtaskUpdate != null)
						{
							WorkerSession.TicketHolder.Update(subtaskUpdate);
						}

						await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, $"Subtask completed: {content}", WorkerSession.CancellationToken);
						subtaskComplete = true;
						break;
					}
					else if (llmResult.ExitReason == LlmExitReason.Completed || llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
					{
						iterationCount++;
						conversation.ResetIteration();

						if (iterationCount >= 7)
						{
							contextResetCount++;

							if (contextResetCount >= 2)
							{
								await WorkerSession.ApiClient.AddActivityLogAsync(ticketId, "Developer exceeded max context resets, giving up on subtask", WorkerSession.CancellationToken);
								content = "Developer exceeded max context resets and could not complete the subtask.";
								break;
							}

							await conversation.FinalizeAsync(WorkerSession.CancellationToken);

							string continuePrompt = $"You were working on subtask '{subtaskName}' but got stuck. Look at the local changes and decide if you should continue or take a fresh approach.\nDescription: {subtaskDescription}\nCall end_subtask tool when complete.";
							ICompaction continueCompaction = new CompactionSummarizer(
								WorkerSession.Prompts.TryGetValue("compaction", out string? cp) ? cp : string.Empty,
								WorkerSession.LlmProxy,
								0.9);

							ToolContext continueContext = new ToolContext(taskId, subtaskId, memories, devContext.SubAgentLlmConfigId, null);
							conversation = new CompactingConversation(systemPrompt, continuePrompt, memories, LlmRole.Developer, continueContext, continueCompaction, $"Developer - {subtaskName} (retry)");
							continueContext.OnMemoriesChanged = conversation.RefreshMemoriesMessage;
							iterationCount = 0;
						}
						else if (iterationCount == 3)
						{
							await conversation.AddUserMessageAsync("You've been working for a while. Are you making progress? If you're stuck, try a different approach. Call end_subtask tool when done.", WorkerSession.CancellationToken);
						}
						else
						{
							await conversation.AddUserMessageAsync("Continue working or call end_subtask tool when done.", WorkerSession.CancellationToken);
						}
					}
					else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
					{
						content = "Developer stopped: cost budget exceeded.";
						break;
					}
					else
					{
						content = $"Developer failed: {llmResult.ErrorMessage}";
						break;
					}
				}

				await conversation.FinalizeAsync(WorkerSession.CancellationToken);

				if (subtaskComplete)
				{
					result = new ToolResult($"Developer completed subtask '{subtaskName}': {content}", false);
				}
				else
				{
					result = new ToolResult($"Developer could not complete subtask '{subtaskName}': {content}", false);
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Developer failed: {ex.Message}", false);
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
