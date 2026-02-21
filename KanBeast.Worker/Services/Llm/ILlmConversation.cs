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

    int Iteration { get; }
    int MaxIterations { get; }
    bool HasReachedMaxIterations { get; }
    void IncrementIteration();

    void AddUserMessage(string content);
    void AddAssistantMessage(ConversationMessage message);
    void AddToolMessage(string toolCallId, string toolResult);
    void AddNote(string content);

    Task RecordCostAsync(decimal cost, CancellationToken cancellationToken);
    decimal GetRemainingBudget();

    Task ResetAsync();

    // Runs periodic maintenance: compaction if context exceeds threshold, and lazy sync if due.
    Task MaintenanceAsync(CancellationToken cancellationToken);

    // Marks the conversation finished, runs handoff compaction if available, and flushes.
    // Returns the compacted summary, or falls back to the LlmResult content/error.
    Task<string> FinalizeAsync(LlmResult llmResult, CancellationToken cancellationToken);

    Task ForceFlushAsync();
}

