using System.Text;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Defines the contract for context compaction strategies.
public interface ICompaction
{
    Task<decimal> CompactAsync(LlmConversation conversation, LlmService llmService, bool jsonFormat, CancellationToken cancellationToken);
}

// Keeps context statements untouched when compaction is disabled.
public class CompactionNone : ICompaction
{
    public Task<decimal> CompactAsync(LlmConversation conversation, LlmService llmService, bool jsonFormat, CancellationToken cancellationToken)
    {
        return Task.FromResult(0m);
    }
}

// Summarizes conversation messages into a compact continuation summary.
public class CompactionSummarizer : ICompaction
{
    private const int MinimumThreshold = 3072;

    private readonly string _compactionPrompt;
    private readonly int _effectiveThreshold;

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

    public async Task<decimal> CompactAsync(LlmConversation conversation, LlmService llmService, bool jsonFormat, CancellationToken cancellationToken)
    {
        int messageSize = GetMessageSize(conversation.Messages);

        if (messageSize > _effectiveThreshold)
        {
            Console.WriteLine($"[Compaction] Context size {messageSize} exceeds threshold {_effectiveThreshold}, summarizing...");

            if (jsonFormat)
            {
                await conversation.WriteLogAsync("-pre-compact", cancellationToken);
            }

            string messagesBlock = BuildMessagesBlock(conversation.Messages);
            string userPrompt = $"{_compactionPrompt}\n\nConversation:\n{messagesBlock}";
            LlmConversation summaryConversation = new LlmConversation(conversation.Model, string.Empty, userPrompt, string.Empty);
            List<IToolProvider> providers = new List<IToolProvider>();
            LlmResult result = await llmService.RunAsync(summaryConversation, providers, LlmRole.Compaction, null, cancellationToken);
            string summary = result.Content;

            // Keep system message, replace everything else with summary as a user message
            ChatMessage? systemMessage = null;
            if (conversation.Messages.Count > 0 && string.Equals(conversation.Messages[0].Role, "system", StringComparison.Ordinal))
            {
                systemMessage = conversation.Messages[0];
            }

            conversation.Messages.Clear();

            if (systemMessage != null)
            {
                conversation.Messages.Add(systemMessage);
            }

            conversation.Messages.Add(new ChatMessage { Role = "user", Content = $"[Continuation summary]\n{summary}" });

            Console.WriteLine($"[Compaction] Reduced to {GetMessageSize(conversation.Messages)} chars");

            return result.AccumulatedCost;
        }

        return 0m;
    }

    private static string BuildMessagesBlock(List<ChatMessage> messages)
    {
        StringBuilder builder = new StringBuilder();

        foreach (ChatMessage message in messages)
        {
            if (string.Equals(message.Role, "system", StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(message.Role);
            builder.Append(": ");
            builder.Append(message.Content ?? "[tool call]");
            builder.Append('\n');
        }

        return builder.ToString();
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
