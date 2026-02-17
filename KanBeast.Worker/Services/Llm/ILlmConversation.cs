using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Common surface for all conversation management strategies.
// Consumers interact through this interface; the concrete implementation
// decides how messages are structured, compacted, and synced.
public interface ILlmConversation
{
    string Id { get; }
    string DisplayName { get; }
    ConversationData Data { get; }
    List<ConversationMessage> Messages { get; }

    LlmRole Role { get; set; }
    ToolContext ToolContext { get; }
    ConversationMemories Memories { get; }

    int Iteration { get; }
    int MaxIterations { get; set; }
    bool HasReachedMaxIterations { get; }
    void IncrementIteration();
    void ResetIteration();

    void AddMemory(string label, string memory);
    bool RemoveMemory(string label, string memoryToRemove);
    void RefreshMemoriesMessage();

    Task AddUserMessageAsync(string content, CancellationToken cancellationToken);
    Task AddAssistantMessageAsync(ConversationMessage message, string modelName, CancellationToken cancellationToken);
    Task AddToolMessageAsync(string toolCallId, string toolResult, CancellationToken cancellationToken);

    Task RecordCostAsync(decimal cost, CancellationToken cancellationToken);
    decimal GetRemainingBudget();

    Task ResetAsync();
    Task FinalizeAsync(CancellationToken cancellationToken);
    Task ForceFlushAsync();
}
