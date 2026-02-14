using System.ComponentModel;
using System.Text;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Defines the contract for context compaction strategies.
public interface ICompaction
{
    Task CompactIfNeededAsync(LlmConversation conversation, CancellationToken cancellationToken);
    Task CompactNowAsync(LlmConversation conversation, CancellationToken cancellationToken);
}

// Keeps context untouched when compaction is disabled.
public class CompactionNone : ICompaction
{
    public Task CompactIfNeededAsync(LlmConversation conversation, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task CompactNowAsync(LlmConversation conversation, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

// Summarizes conversation messages into a compact continuation summary.
//
// COMPACTION STRATEGY:
// Works with the LlmConversation structure: [system][instructions][facts][summaries][conversation...]
//
// When context exceeds threshold:
//   1. Messages 0-3 (system, instructions, facts, summaries) are NEVER compressed
//   2. Messages from index 4 to ~80% mark are summarized
//   3. The summary is added to the chapter summaries list (message 3)
//   4. The summarized messages are removed, keeping only the recent ~20%
//   5. The compaction LLM can hoist important discoveries into the key facts list
//
// This preserves:
//   - Prompt cache efficiency (static prefix stays unchanged)
//   - Important discoveries (hoisted to facts)
//   - Historical context (accumulated in chapter summaries)
//   - Recent context (kept verbatim)
//
public class CompactionSummarizer : ICompaction
{
    private const int MinimumThreshold = 3072;

    private static readonly HashSet<string> ValidLabels = new HashSet<string>
    {
        "INVARIANT",
        "CONSTRAINT",
        "DECISION",
        "REFERENCE",
        "OPEN_ITEM"
    };

    private readonly string _compactionPrompt;
    private readonly LlmProxy _llmProxy;
    private readonly double _contextSizePercent;

    public CompactionSummarizer(string compactionPrompt, LlmProxy llmProxy, double contextSizePercent)
    {
        _compactionPrompt = compactionPrompt;
        _llmProxy = llmProxy;
        _contextSizePercent = contextSizePercent;
    }

    public Task CompactIfNeededAsync(LlmConversation conversation, CancellationToken cancellationToken)
    {
        LlmService? service = _llmProxy.FindAvailableService();
        int contextLength = service?.ContextLength ?? 0;
        int effectiveThreshold;

        if (contextLength > 0 && _contextSizePercent > 0)
        {
            effectiveThreshold = Math.Max(MinimumThreshold, (int)(contextLength * _contextSizePercent));
        }
        else
        {
            effectiveThreshold = MinimumThreshold;
        }

        int messageSize = GetMessageSize(conversation.Messages);

        if (messageSize <= effectiveThreshold)
        {
            return Task.CompletedTask;
        }

        Console.WriteLine($"[Compaction] Context size {messageSize} exceeds threshold {effectiveThreshold}, compacting...");
        return PerformCompactionAsync(conversation, cancellationToken);
    }

    public Task CompactNowAsync(LlmConversation conversation, CancellationToken cancellationToken)
    {
        Console.WriteLine("[Compaction] Forced compaction to hoist memories...");
        return PerformCompactionAsync(conversation, cancellationToken);
    }

    private async Task PerformCompactionAsync(LlmConversation conversation, CancellationToken cancellationToken)
    {
        int startIndex = LlmConversation.FirstCompressibleIndex;
        int totalCompressible = conversation.Messages.Count - startIndex;

        if (totalCompressible < 2)
        {
            return;
        }

        int keepRecentCount = Math.Max(1, (int)(totalCompressible * 0.2));
        int endIndex = conversation.Messages.Count - keepRecentCount;

        if (endIndex <= startIndex)
        {
            return;
        }

		// Get the original task from message 1 (initial instructions)
		string originalTask = conversation.Messages.Count > 1 ? conversation.Messages[1].Content ?? "" : "";

		// Build history block
		string historyBlock = BuildHistoryBlock(conversation.Messages, startIndex, endIndex);

		string userPrompt = $"""
			[Original task]
			{originalTask}
			""";

		ToolContext parentContext = conversation.ToolContext;
		ToolContext compactionContext = new ToolContext(conversation, parentContext.CurrentTaskId, parentContext.CurrentSubtaskId, parentContext.Memories);
		string compactLogDir = conversation.LogDirectory;
		string compactLogPrefix = !string.IsNullOrWhiteSpace(conversation.LogPrefix) ? $"{conversation.LogPrefix}-compact" : string.Empty;
		ICompaction noCompaction = new CompactionNone();

		LlmConversation summaryConversation = new LlmConversation(_compactionPrompt, userPrompt, conversation.Memories, LlmRole.Compaction, compactionContext, noCompaction, compactLogDir, compactLogPrefix, $"{conversation.DisplayName} (Compaction)");
		await summaryConversation.AddUserMessageAsync($"""
			<history>
			{historyBlock}
			</history>

			First, use add_memory and remove_memory to update the memories with important discoveries, decisions, or state changes from this history.
			Finally, call summarize_history with a concise chapter summary as described in the instructions, which completes the compaction process.
			""", cancellationToken);

		for (;;)
		{
			LlmResult result = await _llmProxy.ContinueAsync(summaryConversation, null, cancellationToken);

			if (result.ExitReason == LlmExitReason.ToolRequestedExit && result.FinalToolCalled == "summarize_history")
			{
				conversation.AddChapterSummary(result.Content);
				await conversation.DeleteRangeAsync(startIndex, endIndex, cancellationToken);
				Console.WriteLine($"[Compaction] Reduced to {GetMessageSize(conversation.Messages)} chars, now {conversation.ChapterSummaries.Count} chapters");
				break;
			}
			else if (result.ExitReason == LlmExitReason.Completed)
			{
				await summaryConversation.AddUserMessageAsync("Continue updating memories if needed, then call summarize_history tool with the summary.", cancellationToken);
			}
			else if (result.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				summaryConversation.ResetIteration();
				await summaryConversation.AddUserMessageAsync("Are you making forward progress?  Call summarize_history tool to complete the compaction process.", cancellationToken);
			}
			else if (result.ExitReason == LlmExitReason.CostExceeded)
			{
				Console.WriteLine("[Compaction] Cost budget exceeded during compaction");
				break;
			}
			else
			{
				Console.WriteLine($"[Compaction] LLM failed during compaction: {result.ErrorMessage}");
				break;
			}
		}
	}

    private static string BuildHistoryBlock(List<ConversationMessage> messages, int startIndex, int endIndex)
    {
        StringBuilder builder = new StringBuilder();

        for (int i = startIndex; i < endIndex; i++)
        {
            ConversationMessage message = messages[i];

            if (message.Role == "user")
            {
                builder.Append("user: \"");
                builder.Append(EscapeQuotes(message.Content ?? ""));
                builder.Append("\"\n");
            }
            else if (message.Role == "assistant")
            {
                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    foreach (ConversationToolCall toolCall in message.ToolCalls)
                    {
                        builder.Append("assistant: ");
                        builder.Append(toolCall.Function.Name);
                        builder.Append('(');
                        builder.Append(toolCall.Function.Arguments);
                        builder.Append(")\n");
                    }
                }
                else if (!string.IsNullOrEmpty(message.Content))
                {
                    builder.Append("assistant: \"");
                    builder.Append(EscapeQuotes(message.Content));
                    builder.Append("\"\n");
                }
            }
            else if (message.Role == "tool")
            {
                builder.Append("tool_result: \"");
                builder.Append(EscapeQuotes(message.Content ?? ""));
                builder.Append("\"\n");
            }
        }

        return builder.ToString();
    }

    private static string EscapeQuotes(string text)
    {
        return text.Replace("\"", "\\\"");
    }

	[Description("Add a labeled memory to the persistent memories list. Memories survive compaction and are visible to the agent across the entire conversation.")]
	public static Task<ToolResult> AddMemoryAsync(
		[Description("Label: INVARIANT (what is), CONSTRAINT (what cannot be), DECISION (what was chosen), REFERENCE (what was done), or OPEN_ITEM (what is unresolved)")] string label,
		[Description("Terse, self-contained memory entry")] string content,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(content))
		{
			result = new ToolResult("Error: Content cannot be empty", false);
		}
		else
		{
			string trimmedLabel = label.Trim().ToUpperInvariant();
			if (!ValidLabels.Contains(trimmedLabel))
			{
				result = new ToolResult($"Error: Label must be one of: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM", false);
			}
			else
			{
				context.CompactionTarget!.AddMemory(trimmedLabel, content.Trim());
				result = new ToolResult($"Added [{trimmedLabel}]: {content.Trim()}", false);
			}
		}

		return Task.FromResult(result);
	}

	[Description("Remove a memory that is no longer true, relevant, or has been superseded.")]
	public static Task<ToolResult> RemoveMemoryAsync(
		[Description("Label: INVARIANT, CONSTRAINT, DECISION, REFERENCE, or OPEN_ITEM")] string label,
		[Description("Text to match against existing memories (beginning of the memory entry)")] string memoryToRemove,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(memoryToRemove))
		{
			result = new ToolResult("Error: Memory text cannot be empty", false);
		}
		else
		{
			string trimmedLabel = label.Trim().ToUpperInvariant();
			if (!ValidLabels.Contains(trimmedLabel))
			{
				result = new ToolResult($"Error: Label must be one of: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM", false);
			}
			else
			{
				bool removed = context.CompactionTarget!.RemoveMemory(trimmedLabel, memoryToRemove);
				if (removed)
				{
					result = new ToolResult($"Removed [{trimmedLabel}] memory matching: {memoryToRemove}", false);
				}
				else
				{
					result = new ToolResult($"No matching memory found in [{trimmedLabel}] (need >5 character match at start)", false);
				}
			}
		}

		return Task.FromResult(result);
	}

	[Description("Provide the final summary of the history block and complete the compaction process.")]
	public static Task<ToolResult> SummarizeHistoryAsync(
		[Description("Concise summary of the work done, as it pertains to solving the original task")] string summary,
		ToolContext context)
	{
		ToolResult result = new ToolResult(summary.Trim(), true);
		return Task.FromResult(result);
	}

    private static int GetMessageSize(List<ConversationMessage> messages)
    {
        int size = 0;

        foreach (ConversationMessage message in messages)
        {
            if (string.Equals(message.Role, "system", StringComparison.Ordinal))
            {
                continue;
            }

            size += message.Role.Length + 2;
            size += message.Content?.Length ?? 12;
            size += 1;
        }

        return size;
    }
}
