using System.ComponentModel;
using System.Text;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Defines the contract for context compaction strategies.
public interface ICompaction
{
    Task CompactIfNeededAsync(CompactingConversation conversation, CancellationToken cancellationToken);
    Task CompactNowAsync(CompactingConversation conversation, CancellationToken cancellationToken);
}

// Keeps context untouched when compaction is disabled.
public class CompactionNone : ICompaction
{
    public Task CompactIfNeededAsync(CompactingConversation conversation, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task CompactNowAsync(CompactingConversation conversation, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

// Summarizes conversation messages into a compact continuation summary.
//
// COMPACTION STRATEGY:
// Works with the CompactingConversation structure: [system][instructions][facts][summaries][conversation...]
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
    private const int CharsPerToken = 4;

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

    public Task CompactIfNeededAsync(CompactingConversation conversation, CancellationToken cancellationToken)
    {
        int contextLength = _llmProxy.GetSmallestContextLength();
        int effectiveThreshold;

        if (contextLength > 0 && _contextSizePercent > 0)
        {
            effectiveThreshold = Math.Max(MinimumThreshold, (int)(contextLength * _contextSizePercent));
        }
        else
        {
            effectiveThreshold = MinimumThreshold;
        }

        int estimatedTokens = GetMessageSize(conversation.Messages);

        if (estimatedTokens <= effectiveThreshold)
        {
            return Task.CompletedTask;
        }

        Console.WriteLine($"[Compaction] ~{estimatedTokens} tokens exceeds {effectiveThreshold} threshold ({_contextSizePercent:P0} of {contextLength} smallest context), compacting...");
        return PerformCompactionAsync(conversation, cancellationToken);
    }

    public Task CompactNowAsync(CompactingConversation conversation, CancellationToken cancellationToken)
    {
        Console.WriteLine("[Compaction] Forced compaction to hoist memories...");
        return PerformCompactionAsync(conversation, cancellationToken);
    }

    private async Task PerformCompactionAsync(CompactingConversation conversation, CancellationToken cancellationToken)
    {
        int startIndex = CompactingConversation.FirstCompressibleIndex;
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
		ToolContext compactionContext = new ToolContext(parentContext.CurrentTaskId, parentContext.CurrentSubtaskId, parentContext.Memories, null, null);
		compactionContext.OnMemoriesChanged = conversation.RefreshMemoriesMessage;
		string compactLogPrefix = !string.IsNullOrWhiteSpace(conversation.DisplayName) ? $"{conversation.DisplayName} (Compaction)" : "Compaction";
		ICompaction noCompaction = new CompactionNone();

		CompactingConversation summaryConversation = new CompactingConversation(_compactionPrompt, userPrompt, conversation.Memories, LlmRole.Compaction, compactionContext, noCompaction, compactLogPrefix);
		await summaryConversation.AddUserMessageAsync($"""
			<history>
			{historyBlock}
			</history>

			First, use add_memory and remove_memory to update the memories with important discoveries, decisions, or state changes from this history.
			Finally, call summarize_history with a concise chapter summary as described in the instructions, which completes the compaction process.
			""", cancellationToken);

		for (int compactionAttempt = 0; compactionAttempt < 3; compactionAttempt++)
		{
			LlmResult result = await _llmProxy.ContinueAsync(summaryConversation, null, cancellationToken);

			if (result.ExitReason == LlmExitReason.ToolRequestedExit && result.FinalToolCalled == "summarize_history")
			{
				conversation.AddChapterSummary(result.Content);
				await conversation.DeleteRangeAsync(startIndex, endIndex, cancellationToken);
				Console.WriteLine($"[Compaction] Reduced to ~{GetMessageSize(conversation.Messages)} tokens, now {conversation.ChapterSummaries.Count} chapters");
				return;
			}
			else if (result.ExitReason == LlmExitReason.Completed || result.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				summaryConversation.ResetIteration();
				await summaryConversation.AddUserMessageAsync("Call summarize_history tool with the summary to complete the compaction process.", cancellationToken);
			}
			else if (result.ExitReason == LlmExitReason.CostExceeded)
			{
				Console.WriteLine("[Compaction] Cost budget exceeded during compaction");
				return;
			}
			else
			{
				Console.WriteLine($"[Compaction] LLM failed during compaction: {result.ErrorMessage}");
				return;
			}
		}

		Console.WriteLine("[Compaction] LLM did not call summarize_history after 3 attempts, skipping compaction");
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
				context.Memories.Add(trimmedLabel, content.Trim());
				context.OnMemoriesChanged?.Invoke();
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
				bool removed = context.Memories.Remove(trimmedLabel, memoryToRemove);
				if (removed)
				{
					context.OnMemoriesChanged?.Invoke();
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

    // Estimates the token count of the conversation using ~4 characters per token.
    // ContextLength from LLM configs is in tokens, so this must return tokens too.
    // Includes system messages and a fixed overhead for tool definitions.
    private static int GetMessageSize(List<ConversationMessage> messages)
    {
        int chars = 0;

        foreach (ConversationMessage message in messages)
        {
            chars += message.Role.Length + 2;
            chars += message.Content?.Length ?? 12;

            if (message.ToolCalls != null)
            {
                foreach (ConversationToolCall toolCall in message.ToolCalls)
                {
                    chars += toolCall.Function.Name.Length;
                    chars += toolCall.Function.Arguments?.Length ?? 0;
                }
            }

            chars += 1;
        }

        // Tool definitions sent in the request consume context but aren't in messages.
        int toolDefinitionOverhead = 2000;

        return (chars / CharsPerToken) + toolDefinitionOverhead;
    }
}
