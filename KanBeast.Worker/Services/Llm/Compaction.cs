using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Defines the contract for context compaction strategies.
public interface ICompaction
{
    Task<decimal> CompactAsync(LlmConversation conversation, LlmService llmService, decimal remainingBudget, CancellationToken cancellationToken);
}

// Keeps context untouched when compaction is disabled.
public class CompactionNone : ICompaction
{
    public Task<decimal> CompactAsync(LlmConversation conversation, LlmService llmService, decimal remainingBudget, CancellationToken cancellationToken)
    {
        return Task.FromResult(0m);
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

    private readonly string _compactionPrompt;
    private readonly int _effectiveThreshold;

    private LlmConversation? _targetConversation;

    public CompactionSummarizer(string compactionPrompt, int contextSizeThreshold)
    {
        _compactionPrompt = compactionPrompt;

        if (contextSizeThreshold > 0)
        {
            _effectiveThreshold = Math.Max(MinimumThreshold, contextSizeThreshold);
        }
        else
        {
            _effectiveThreshold = MinimumThreshold;
        }
    }

    public async Task<decimal> CompactAsync(LlmConversation conversation, LlmService llmService, decimal remainingBudget, CancellationToken cancellationToken)
    {
        int messageSize = GetMessageSize(conversation.Messages);

        if (messageSize <= _effectiveThreshold)
        {
            return 0m;
        }

        Console.WriteLine($"[Compaction] Context size {messageSize} exceeds threshold {_effectiveThreshold}, summarizing...");

        int startIndex = LlmConversation.FirstCompressibleIndex;
        int totalCompressible = conversation.Messages.Count - startIndex;

        if (totalCompressible < 2)
        {
            return 0m;
        }

        int keepRecentCount = Math.Max(1, (int)(totalCompressible * 0.2));
        int endIndex = conversation.Messages.Count - keepRecentCount;

        if (endIndex <= startIndex)
        {
            return 0m;
        }

        _targetConversation = conversation;

        // Get the original task from message 1 (initial instructions)
        string originalTask = conversation.Messages.Count > 1 ? conversation.Messages[1].Content ?? "" : "";

        // Build memories section
        string memoriesSection = conversation.Memories.Count > 0
            ? "[Current memories]\n" + string.Join("\n", conversation.Memories.Select(m => $"- {m}"))
            : "[Current memories]\nNone";

        // Build history block
        string historyBlock = BuildHistoryBlock(conversation.Messages, startIndex, endIndex);

        string userPrompt = $"""
            [Original task]
            {originalTask}

            {memoriesSection}

            <history>
            {historyBlock}
            </history>

            First, use add_memory and remove_memory to update the memories with important discoveries, decisions, or state changes from this history.
            Then respond with a concise summary of the work done, as it pertains to solving the original task.
            """;

        List<Tool> compactionTools = BuildCompactionTools();
        LlmConversation summaryConversation = new LlmConversation(conversation.Model, _compactionPrompt, userPrompt, false, string.Empty, string.Empty);
        LlmResult result = await llmService.RunAsync(summaryConversation, compactionTools, null, remainingBudget, false, cancellationToken);

        _targetConversation = null;

        // The final response is the summary
        if (result.Success && !string.IsNullOrWhiteSpace(result.Content))
        {
            conversation.AddChapterSummary(result.Content);
            await conversation.DeleteRangeAsync(startIndex, endIndex, cancellationToken);
            Console.WriteLine($"[Compaction] Reduced to {GetMessageSize(conversation.Messages)} chars, now {conversation.ChapterSummaries.Count} chapters");
        }

        return result.AccumulatedCost;
    }

    private static string BuildHistoryBlock(List<ChatMessage> messages, int startIndex, int endIndex)
    {
        StringBuilder builder = new StringBuilder();

        for (int i = startIndex; i < endIndex; i++)
        {
            ChatMessage message = messages[i];

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
                    foreach (ToolCallMessage toolCall in message.ToolCalls)
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

    private List<Tool> BuildCompactionTools()
    {
        List<Tool> tools = new List<Tool>();
        ToolHelper.AddTools(tools, this,
            nameof(AddMemoryAsync),
            nameof(RemoveMemoryAsync));
        return tools;
    }

    [Description("Add a labeled memory to the persistent memories list. Memories survive compaction and are visible to the agent across the entire conversation.")]
    public Task<ToolResult> AddMemoryAsync(
        [Description("Label: INVARIANT (what is), CONSTRAINT (what cannot be), DECISION (what was chosen), REFERENCE (what was done), or OPEN_ITEM (what is unresolved)")] string label,
        [Description("Terse, self-contained memory entry")] string content)
    {
        ToolResult result;

        if (_targetConversation == null)
        {
            result = new ToolResult("Error: No active conversation");
        }
        else if (string.IsNullOrWhiteSpace(content))
        {
            result = new ToolResult("Error: Content cannot be empty");
        }
        else
        {
            string trimmedLabel = (label ?? "REFERENCE").Trim().ToUpperInvariant();
            string entry = $"[{trimmedLabel}] {content.Trim()}";
            _targetConversation.AddMemory(entry);
            result = new ToolResult($"Added: {entry}");
        }

        return Task.FromResult(result);
    }

    [Description("Remove a memory that is no longer true, relevant, or has been superseded.")]
    public Task<ToolResult> RemoveMemoryAsync(
        [Description("Text to match against existing memories (beginning of the memory entry)")] string memoryToRemove)
    {
        ToolResult result;

        if (_targetConversation == null)
        {
            result = new ToolResult("Error: No active conversation");
        }
        else if (string.IsNullOrWhiteSpace(memoryToRemove))
        {
            result = new ToolResult("Error: Memory text cannot be empty");
        }
        else
        {
            bool removed = _targetConversation.RemoveMemory(memoryToRemove);
            if (removed)
            {
                result = new ToolResult($"Removed memory matching: {memoryToRemove}");
            }
            else
            {
                result = new ToolResult("No matching memory found (need >5 character match at start)");
            }
        }

        return Task.FromResult(result);
    }

    private static int GetMessageSize(List<ChatMessage> messages)
    {
        int size = 0;

        foreach (ChatMessage message in messages)
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
