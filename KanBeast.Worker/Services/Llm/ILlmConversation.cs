using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Common surface for all conversation management strategies.
// Consumers interact through this interface; the concrete implementation
// decides how messages are structured, compacted, and synced.
public interface ILlmConversation
{
    string Id { get; }
    List<ConversationMessage> Messages { get; }

    LlmRole Role { get; set; }
    ToolContext ToolContext { get; }

    bool HasReachedMaxIterations { get; }
    void IncrementIteration();

    Task AddUserMessageAsync(string content, CancellationToken cancellationToken);
    Task AddAssistantMessageAsync(ConversationMessage message, string modelName, CancellationToken cancellationToken);
    Task AddToolMessageAsync(string toolCallId, string toolResult, CancellationToken cancellationToken);

    Task RecordCostAsync(decimal cost, CancellationToken cancellationToken);
    decimal GetRemainingBudget();

    Task ResetAsync();

    // Marks the conversation finished, runs handoff compaction if available, and flushes.
    // Returns the compacted summary, or falls back to the LlmResult content/error.
    Task<string> FinalizeAsync(LlmResult llmResult, CancellationToken cancellationToken);

    Task ForceFlushAsync();
}

