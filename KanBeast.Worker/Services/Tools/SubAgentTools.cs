using System.ComponentModel;

namespace KanBeast.Worker.Services.Tools;

// Tool that spawns a sub-agent LLM conversation to work on a task in parallel.
public static class SubAgentTools
{
	[Description("""
		Launch a sub-agent to handle complex, multi-step tasks autonomously.
		The sub-agent has the same capabilities as you (shell, files, search, web) except it cannot start further sub-agents or create tasks/subtasks. 

		When to use:
		- For researching complex questions, searching for code, and executing multi-step tasks. When you are searching for a keyword or file and are not confident that you will find the right match in the first few tries use a sub-agent to perform the search for you.
		- Summarizing or analyzing documentation, web pages, or search results. If you want to understand the content of a long document or a set of documents, use a sub-agent to read and summarize them for you.
		- Generating code based on complex requirements. A sub-agent can be your hands where you do the thinking and it does incremental coding tasks. Realize that after it responds, the files it modified will have changed and you should read them again.

		When NOT to use the start_sub_agent tool:
		- If you want to read a specific file path and see the exact code, use the read_file or glob or grep instead of the start_sub_agent tool, to find the match more quickly
		- If you are searching for a specific class definition like \"class Foo\", use the glob or grep tool to find the match more quickly
		- If you are searching for code within a specific file or set of 2-3 files, use the Read tool instead of the Agent tool, to find the match more quickly
		
		Usage notes:
		1. Launch multiple agents concurrently whenever possible to maximize performance; to do that, use a single message with multiple tool uses
		2. Each agent invocation is stateless. You will not be able to send additional messages to the agent, nor will the agent be able to communicate with you outside of its final report. Therefore, your prompt should contain a highly detailed task description for the agent to perform autonomously and you should specify exactly what information the agent should return back to you in its final and only message to you.
		3. The agent's outputs should generally be trusted
		4. Each agent is provided the current set of Memories, but it will not be able to modify them. It has the same system prompt as you, but no further context. Be precise and explicit about its permissions and limitations in the instructions so it understands its duty, and provides value to help advance the task in development.
		5. Clearly tell the agent whether you expect it to write code or just to do research (search, file reads, web fetches, etc.), since it is not aware of the context outside of your instructions.

		The sub-agent runs to completion and returns its final result as requested in the instructions.
		""")]
	public static async Task<ToolResult> StartSubAgentAsync(
		[Description("Clear, self-contained instructions for what the sub-agent needs to accomplish and detailed expectations for its response. You will not see anything else, so be specific.")] string instructions,
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

				LlmMemories memories = new LlmMemories(context.Memories);  // copy the original memories to avoid subagents modifying the original one
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
