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

    LlmRole Role { get; }
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
    Task FinalizeAsync(CancellationToken cancellationToken);
    Task ForceFlushAsync();

    // Returns additional tools provided by the conversation strategy (e.g., memory tools, SFCM push/pop).
    // Appended to the role-based tools for each LLM call.
    IReadOnlyList<Tool> GetAdditionalTools();

    // Called when the model produces text without tool calls.
    // Returns true if the conversation handled it (e.g., injected a nudge) and the agentic loop should continue.
    // Returns false if the response should be treated as a completed turn.
    Task<bool> HandleTextResponseAsync(string text, CancellationToken cancellationToken);
}

// Creates conversation instances. Resolves prompts and compaction config from WorkerSession.
public static class LlmConversationFactory
{
    // Creates a new conversation of the specified type.
    public static ILlmConversation Create(string conversationType, string systemPrompt, string userPrompt, ConversationMemories memories, LlmRole role, ToolContext toolContext, string displayName)
    {
        if (conversationType == "sfcm")
        {
            return new SfcmConversation(systemPrompt, userPrompt, memories, role, toolContext, displayName);
        }

        string compactionPrompt = WorkerSession.Prompts["compaction"];
        double contextSizePercent = WorkerSession.Compaction.ContextSizePercent;
        return new CompactingConversation(systemPrompt, userPrompt, memories, role, toolContext, compactionPrompt, contextSizePercent, displayName);
    }

    // Reconstitutes a conversation from server data, using the stored ConversationType to pick the strategy.
    public static ILlmConversation Reconstitute(ConversationData data, LlmRole role, ToolContext toolContext)
    {
        if (data.ConversationType == "sfcm")
        {
            return new SfcmConversation(data, role, toolContext);
        }

        string compactionPrompt = WorkerSession.Prompts["compaction"];
        double contextSizePercent = WorkerSession.Compaction.ContextSizePercent;
        return new CompactingConversation(data, role, toolContext, compactionPrompt, contextSizePercent);
    }
}
