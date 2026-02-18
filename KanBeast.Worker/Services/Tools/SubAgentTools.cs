using System.ComponentModel;
using KanBeast.Shared;

namespace KanBeast.Worker.Services.Tools;

// Tool that spawns a sub-agent LLM conversation to work on a task in parallel.
public static class SubAgentTools
{
	[Description("""
		Launch a sub-agent to handle complex, multi-step tasks autonomously. The sub-agent has the same capabilities as you (shell, files, search, web).

		When to use:
		- For researching complex questions, searching for code that you aren't certain will be located on the first attempt, and executing multi-step tasks.
		- Digging through documentation, summarizing code, web pages, or search results. 
		- Reading long documents or code files.  Rather than read a lot of files to learn how something works, use a subagent to do that.
		- Generating code based on complex requirements. A sub-agent can be your hands where you do the thinking and it does incremental coding tasks. Realize that after it responds, the files it modified will have changed and you might want to read them again.

		When NOT to use the start_sub_agent tool:
		- If you want to read a specific file path and see the exact code, or make an edit, use the read_file or glob or grep instead of the start_sub_agent tool, to find the match more quickly
		- If you are searching for a specific class definition like \"class Foo\", use the glob or grep tool to find the match quickly
		- If you are searching for code within a specific file or set of 2-3 files, use the Read tool instead of the Agent tool, to find the match more quickly

		Usage notes:
		1. Launch multiple agents concurrently whenever possible to maximize performance; to do that, use a single message with multiple tool uses
		2. Each agent invocation begins with a system prompt, the memories list, and instructions passed in here. It has access to the same repo workspace as you do. 
		3. You will not be able to send additional messages to the agent, and it will only be able to provide a final report of its actions.  Therefore, your instructions should be highly detailed task description for the agent to perform autonomously, specifying exactly what information the agent should return back to you in its response.
		4. The agent's outputs should generally be trusted unless there is evidence to the contrary.
		5. Clearly tell the agent whether you expect it to write code, learn (and explain) how something works, research and make recommendations about something, etc.
		6. After each sub-agent returns, evaluate its performance in your end_subtask summary (25 words max per sub-agent). Note what it did well and what it struggled with.  If you didn't use any subagents, don't mention them.
		""")]
	public static async Task<ToolResult> StartSubAgentAsync(
		[Description("Brief summary of the sub-agent's mission (logged to activity feed and shown to the sub-agent as context).")] string taskSummary,
		[Description("Detailed, self-contained instructions and expectations for the sub-agent's response.")] string instructions,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(taskSummary))
		{
			result = new ToolResult("Error: taskSummary cannot be empty", false, false);
		}
		else if (string.IsNullOrWhiteSpace(instructions))
		{
			result = new ToolResult("Error: instructions cannot be empty", false, false);
		}
		else
		{
			try
			{
				await WorkerSession.ApiClient.AddActivityLogAsync(
					WorkerSession.TicketHolder.Ticket.Id, $"Sub-agent: {taskSummary}", WorkerSession.CancellationToken);

				string fullInstructions = $"{taskSummary}\n\n{instructions}";

				string systemPrompt = WorkerSession.Prompts["subagent"];

				ConversationMemories memories = new ConversationMemories(context.Memories);
					ToolContext subContext = new ToolContext(context.CurrentTaskId, context.CurrentSubtaskId, memories, null, null);

					ILlmConversation conversation = LlmConversationFactory.Create(
						WorkerSession.ConversationType,
						systemPrompt,
						fullInstructions,
						memories,
						LlmRole.SubAgent,
						subContext,
						$"Sub-agent: {taskSummary}");

				string content = string.Empty;

				string? subAgentConfigId = context.SubAgentLlmConfigId;

				for (; ; )
				{
					LlmResult llmResult = !string.IsNullOrWhiteSpace(subAgentConfigId)
						? await WorkerSession.LlmProxy.ContinueWithConfigIdAsync(subAgentConfigId, conversation, null, WorkerSession.CancellationToken)
						: await WorkerSession.LlmProxy.ContinueAsync(conversation, null, WorkerSession.CancellationToken);

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

				result = new ToolResult(content, false, false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Sub-agent failed: {ex.Message}", false, false);
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
			toolResult = new ToolResult("Error: Result cannot be empty", false, false);
		}
		else
		{
			toolResult = new ToolResult(result, true, false);
		}

		return Task.FromResult(toolResult);
	}
}
