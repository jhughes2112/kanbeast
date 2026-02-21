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
		[Description("The LLM config id to use for this sub-agent. Choose from the available LLMs listed in your prompt.")] string llmConfigId,
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
		else if (string.IsNullOrWhiteSpace(llmConfigId))
		{
			result = new ToolResult("Error: llmConfigId cannot be empty", false, false);
		}
		else
		{
			try
			{
				string content = await RunAgentConversationAsync(taskSummary, instructions, LlmRole.DeveloperSubagent, "Sub-agent", llmConfigId, context);
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

	[Description("Call this to signify the end of your assigned task. Compaction will produce the real summary for the parent agent.")]
	public static Task<ToolResult> AgentTaskCompleteAsync(
		[Description("Brief note about what was accomplished, or empty to signal completion.")] string result,
		ToolContext context)
	{
		string response = string.IsNullOrWhiteSpace(result) ? "Task complete." : result;
		ToolResult toolResult = new ToolResult(response, true, false);
		return Task.FromResult(toolResult);
	}

	[Description("""
		Launch a read-only inspection agent to investigate the repository and answer questions.
		The agent can read files, run shell commands, search code, and browse the web, but cannot modify any files.

		When to use:
		- To understand the current state of the codebase before creating tasks
		- To investigate how something is implemented or where specific code lives
		- To research technical approaches or validate assumptions
		- To read documentation, summarize code structure, or answer architectural questions

		Usage notes:
		1. Provide a clear question or investigation goal in the instructions.
		2. The agent works autonomously and returns its findings. You cannot send follow-up messages.
		3. Launch multiple inspection agents concurrently for different questions by issuing multiple tool calls in one message.
		""")]
	public static async Task<ToolResult> StartInspectionAgentAsync(
		[Description("Brief summary of what to investigate (logged to activity feed).")] string taskSummary,
		[Description("Detailed question or investigation instructions. Be specific about what information to return.")] string instructions,
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
				string content = await RunAgentConversationAsync(taskSummary, instructions, LlmRole.PlanningSubagent, "Inspection", context.LlmConfigId, context);
				result = new ToolResult(content, false, false);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Inspection agent failed: {ex.Message}", false, false);
			}
		}

		return result;
	}

	// Shared agent conversation lifecycle: create/reconstitute, run to completion, finalize with compaction.
	// Resolves the LLM service from the registry by config ID. Returns an error string if not found.
	private static async Task<string> RunAgentConversationAsync(
		string taskSummary,
		string instructions,
		LlmRole role,
		string displayPrefix,
		string? llmConfigId,
		ToolContext context)
	{
		await WorkerSession.ApiClient.AddActivityLogAsync(WorkerSession.TicketHolder.Ticket.Id, $"{displayPrefix}: {taskSummary}", WorkerSession.CancellationToken);

		LlmService? service = !string.IsNullOrWhiteSpace(llmConfigId) ? WorkerSession.LlmProxy.GetService(llmConfigId) : null;
		if (service == null)
		{
			return $"{displayPrefix} failed: LLM config '{llmConfigId}' not found";
		}

		string fullInstructions = $"{taskSummary}\n\n{instructions}";

		ToolContext subContext = new ToolContext(context.CurrentTaskId, context.CurrentSubtaskId, service, null);

		// Check for a prior conversation from a crashed run. If found, reconstitute it.
		ILlmConversation conversation;
		string? toolCallId = ToolContext.ActiveToolCallId;

		ConversationData? existing = toolCallId != null
			? await WorkerSession.ApiClient.GetConversationAsync(WorkerSession.TicketHolder.Ticket.Id, toolCallId, WorkerSession.CancellationToken)
			: null;

		if (existing != null)
		{
			conversation = new CompactingConversation(existing, role, subContext, null, null, null);
			Console.WriteLine($"[{displayPrefix}] Reconstituted conversation {toolCallId}");
		}
		else
		{
			conversation = new CompactingConversation(null, role, subContext, fullInstructions, $"{displayPrefix}: {taskSummary}", toolCallId);
		}

		LlmResult llmResult;
		await WorkerSession.HubClient.SetConversationBusyAsync(conversation.Id, true);
		try
		{
			llmResult = await service.RunToCompletionAsync(conversation, null, false, true, context.CancellationToken);
		}
		finally
		{
			await WorkerSession.HubClient.SetConversationBusyAsync(conversation.Id, false);
		}

		if (!llmResult.Success)
		{
			return $"{displayPrefix} failed: {llmResult.Content}";
		}

		return llmResult.Content;
	}
}
