using System.ComponentModel;

namespace KanBeast.Worker.Services.Tools;

// Tool that spawns a sub-agent LLM conversation to work on a task in parallel.
public static class SubAgentTools
{
	[Description("Start a sub-agent to work on a task independently. The sub-agent has the same capabilities as you (shell, files, search, web) except it cannot start further sub-agents or create tasks/subtasks. Use this to parallelize independent work. The sub-agent runs to completion and returns its final result.")]
	public static async Task<ToolResult> StartSubAgentAsync(
		[Description("Clear, self-contained instructions for what the sub-agent should accomplish")] string instructions,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(instructions))
		{
			result = new ToolResult("Error: Instructions cannot be empty", false);
		}
		else
		{
			try
			{
				string systemPrompt = WorkerSession.Prompts.TryGetValue("subagent", out string? prompt) ? prompt : string.Empty;

				LlmMemories memories = new LlmMemories();
				ICompaction compaction = new CompactionNone();
				ToolContext subContext = new ToolContext(null, context.CurrentTaskId, context.CurrentSubtaskId, memories);

				string ticketId = WorkerSession.TicketHolder.Ticket.Id;
				LlmConversation conversation = new LlmConversation(
					systemPrompt,
					instructions,
					memories,
					LlmRole.SubAgent,
					subContext,
					compaction,
					false,
					"/workspace/logs",
					$"TIK-{ticketId}-sub");

				string content = string.Empty;

				for (; ; )
				{
					LlmResult llmResult = await WorkerSession.LlmProxy.ContinueAsync(conversation, null, WorkerSession.CancellationToken);

					if (llmResult.ExitReason == LlmExitReason.Completed)
					{
						content = llmResult.Content;
						break;
					}
					else if (llmResult.ExitReason == LlmExitReason.ToolRequestedExit)
					{
						content = llmResult.Content;
						break;
					}
					else if (llmResult.ExitReason == LlmExitReason.MaxIterationsReached)
					{
						conversation.ResetIteration();
						await conversation.AddUserMessageAsync("Continue working. Call agent_task_complete with your result when done.", WorkerSession.CancellationToken);
					}
					else if (llmResult.ExitReason == LlmExitReason.CostExceeded)
					{
						content = "Sub-agent stopped: cost budget exceeded";
						break;
					}
					else
					{
						content = $"Sub-agent failed: {llmResult.ErrorMessage}";
						break;
					}
				}

				await conversation.FinalizeAsync(WorkerSession.CancellationToken);

				result = new ToolResult(content, false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Sub-agent failed: {ex.Message}", false);
			}
		}

		return result;
	}

	[Description("You MUST call this to signify the end of your assigned task.")]
	public static Task<ToolResult> AgentTaskCompleteAsync(
		[Description("Supply only the requested information or an explanation of what went wrong.")] string result,
		ToolContext context)
	{
		ToolResult toolResult;

		if (string.IsNullOrWhiteSpace(result))
		{
			toolResult = new ToolResult("Error: Result cannot be empty", false);
		}
		else
		{
			toolResult = new ToolResult(result, true);
		}

		return Task.FromResult(toolResult);
	}
}
