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
    void ResetIteration();

    Task AddUserMessageAsync(string content, CancellationToken cancellationToken);
    Task AddAssistantMessageAsync(ConversationMessage message, string modelName, CancellationToken cancellationToken);
    Task AddToolMessageAsync(string toolCallId, string toolResult, CancellationToken cancellationToken);

    Task RecordCostAsync(decimal cost, CancellationToken cancellationToken);
    decimal GetRemainingBudget();

    Task ResetAsync();

    // Marks the conversation finished, runs handoff compaction if available, and flushes.
    // Returns the compacted summary for the parent, or null if compaction was unavailable/failed.
    Task<string?> FinalizeAsync(CancellationToken cancellationToken);

    Task ForceFlushAsync();
}

// Creates conversation instances.
public static class LlmConversationFactory
{
    public static ILlmConversation Create(LlmRole role, ToolContext toolContext, string userPrompt, string displayName, string? id)
    {
        return new CompactingConversation(null, role, toolContext, userPrompt, displayName, id);
    }

    // Reconstitutes a conversation from server data.
    public static ILlmConversation Reconstitute(ConversationData data, LlmRole role, ToolContext toolContext)
    {
        return new CompactingConversation(data, role, toolContext, null, null, null);
    }
}
