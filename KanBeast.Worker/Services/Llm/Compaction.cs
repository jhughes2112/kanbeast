using System.Text;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Defines the contract for context compaction strategies.
public interface ICompaction
{
    Task CompactAsync(LlmConversation conversation, LlmService llmService, string logDirectory, string logPrefix, CancellationToken cancellationToken);
}

// Keeps context statements untouched when compaction is disabled.
public class CompactionNone : ICompaction
{
    public Task CompactAsync(LlmConversation conversation, LlmService llmService, string logDirectory, string logPrefix, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
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

    public async Task CompactAsync(LlmConversation conversation, LlmService llmService, string logDirectory, string logPrefix, CancellationToken cancellationToken)
    {
        int messageSize = GetMessageSize(conversation.Messages);

        if (messageSize > _effectiveThreshold)
        {
            string messagesBlock = BuildMessagesBlock(conversation.Messages);
            string userPrompt = $"{_compactionPrompt}\n\nConversation:\n{messagesBlock}";
            LlmConversation summaryConversation = new LlmConversation(conversation.Model, string.Empty, userPrompt);
            List<IToolProvider> providers = new List<IToolProvider>();
            string summary = await llmService.RunAsync(summaryConversation, providers, LlmRole.Compaction, cancellationToken);

            await conversation.WriteLogAsync(logDirectory, logPrefix, "pre-compact", cancellationToken);

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
        }
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
