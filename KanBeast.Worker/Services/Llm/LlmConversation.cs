using System.Text;
using System.Text.Json.Serialization;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Holds the content and sync state for a single LLM conversation.
//
// CONVERSATION STRUCTURE:
// The conversation maintains a specific message structure for prompt cache efficiency and compaction:
//   [0] System prompt - Static instructions for the LLM role
//   [1] Initial instructions - The first user message with task details
//   [2] Memories - A dynamically updated message containing accumulated discoveries (may be empty initially)
//   [3] Chapter summaries - Accumulated summaries from previous compactions (may be empty initially)
//   [4+] Conversation messages - Regular assistant/user/tool exchanges
//
// During compaction:
//   - Messages 0-3 are preserved (system, instructions, memories, summaries)
//   - Messages 4 to ~80% are summarized and added to the chapter summaries
//   - The most recent ~20% of messages are kept intact
//   - Important discoveries can be hoisted into the memories list
//   - Each compaction adds a new chapter summary, building a history
//
// LAZY SYNC:
// Rather than requiring explicit SyncToServerAsync calls, the conversation tracks when it first
// became dirty via a UTC timestamp. Any awaited operation will auto-sync if 5+ seconds have elapsed.
//
public class LlmConversation
{
    private const long SyncDelayTicks = TimeSpan.TicksPerSecond * 5;

    private readonly ConversationMemories _memories;
    private readonly ICompaction _compaction;
    private long _dirtyTimestamp;

    // The serializable core — this is what gets synced to the server.
    public ConversationData Data { get; }

    // Convenience accessors that delegate to Data.
    public string Id => Data.Id;
    public string DisplayName => Data.DisplayName;
    public List<ConversationMessage> Messages => Data.Messages;

    public LlmConversation(string systemPrompt, string userPrompt, ConversationMemories memories, LlmRole role, ToolContext toolContext, ICompaction compaction, string displayName)
    {
        Data = new ConversationData
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            StartedAt = DateTime.UtcNow.ToString("O"),
            Memories = memories.Backing
        };

        Role = role;
        ToolContext = toolContext;
        _memories = memories;
        _compaction = compaction;
        _dirtyTimestamp = DateTime.UtcNow.Ticks;

        // Message 0: System prompt
        Messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });

        // Message 1: Initial instructions
        Messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });

        // Message 2: Memories (reflects current state of shared memories)
        Messages.Add(new ConversationMessage { Role = "assistant", Content = _memories.Format() });

        // Message 3: Chapter summaries (empty initially)
        Messages.Add(new ConversationMessage { Role = "assistant", Content = "[Chapter summaries: None yet]" });
    }

    // Restores a conversation from server data. Memories are reconstituted from Data.Memories.
    public LlmConversation(ConversationData data, LlmRole role, ToolContext toolContext, ICompaction compaction)
    {
        Data = data;
        _memories = new ConversationMemories(data.Memories);
        Role = role;
        ToolContext = toolContext;
        _compaction = compaction;
        _dirtyTimestamp = 0;
    }

	// Index of the first compressible message (after system, instructions, memories, and summaries)
	public const int FirstCompressibleIndex = 4;

	[JsonIgnore]
	public ConversationMemories Memories => _memories;

	[JsonIgnore]
	public IReadOnlyList<string> ChapterSummaries => Data.ChapterSummaries;

	[JsonIgnore]
	public ICompaction Compaction => _compaction;

	[JsonIgnore]
	public LlmRole Role { get; set; }

	[JsonIgnore]
	public ToolContext ToolContext { get; }

	[JsonIgnore]
	public int Iteration { get; private set; }

	public int MaxIterations { get; set; } = 25;

	public bool HasReachedMaxIterations => Iteration >= MaxIterations;

	public void IncrementIteration()
	{
		Iteration++;
	}

	public void ResetIteration()
	{
		Iteration = 0;
	}

	public void AddMemory(string label, string memory)
	{
		_memories.Add(label, memory);
		UpdateMemoriesMessage();
	}

	public bool RemoveMemory(string label, string memoryToRemove)
	{
		bool removed = _memories.Remove(label, memoryToRemove);
		if (removed)
		{
			UpdateMemoriesMessage();
		}

		return removed;
	}

	private const int MaxChapterSummaries = 10;

    public void AddChapterSummary(string summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        if (Data.ChapterSummaries.Count >= MaxChapterSummaries)
        {
            Data.ChapterSummaries.RemoveAt(0);
        }

        Data.ChapterSummaries.Add(summary);
        UpdateSummariesMessage();
    }

    // Refreshes message[2] to reflect the current state of memories. Called by tools via OnMemoriesChanged.
    public void RefreshMemoriesMessage()
    {
        UpdateMemoriesMessage();
    }

    private void UpdateMemoriesMessage()
    {
        if (Messages.Count < 4)
        {
            return;
        }

        Messages[2] = new ConversationMessage { Role = "assistant", Content = _memories.Format() };
        MarkDirty();
    }

    private void UpdateSummariesMessage()
    {
        if (Messages.Count < 4)
        {
            return;
        }

        string summariesContent;
        if (Data.ChapterSummaries.Count == 0)
        {
            summariesContent = "[Chapter summaries: None yet]";
        }
        else
        {
            StringBuilder sb = new StringBuilder("[Chapter summaries]\n");
            for (int i = 0; i < Data.ChapterSummaries.Count; i++)
            {
                sb.Append($"### Chapter {i + 1}\n{Data.ChapterSummaries[i]}");
                if (i < Data.ChapterSummaries.Count - 1)
                {
                    sb.Append("\n\n");
                }
            }
            summariesContent = sb.ToString();
        }

        Messages[3] = new ConversationMessage { Role = "assistant", Content = summariesContent };
        MarkDirty();
    }

	public async Task AddUserMessageAsync(string content, CancellationToken cancellationToken)
	{
		ConversationMessage message = new ConversationMessage
		{
			Role = "user",
			Content = content
		};
		Messages.Add(message);
		Console.WriteLine($"[{DisplayName}] User: {(content.Length > 50 ? content.Substring(0, 50) + "..." : content)}");
		MarkDirty();

		await _compaction.CompactIfNeededAsync(this, cancellationToken);
		await LazySyncIfDueAsync();
	}

	public async Task AddAssistantMessageAsync(ConversationMessage message, string modelName, CancellationToken cancellationToken)
	{
		Messages.Add(message);
		string preview = message.Content?.Length > 50 ? message.Content.Substring(0, 50) + "..." : message.Content ?? "[no content]";
		if (message.ToolCalls?.Count > 0)
		{
			preview = $"[{message.ToolCalls.Count} tool call(s)] {preview}";
		}
		Console.WriteLine($"[{DisplayName}] ({modelName}) Assistant: {preview}");
		MarkDirty();

		await _compaction.CompactIfNeededAsync(this, cancellationToken);
		await LazySyncIfDueAsync();
	}

	public async Task AddToolMessageAsync(string toolCallId, string toolResult, CancellationToken cancellationToken)
	{
		ConversationMessage message = new ConversationMessage
		{
			Role = "tool",
			Content = toolResult,
			ToolCallId = toolCallId
		};
		Messages.Add(message);
		string preview = toolResult.Length > 50 ? toolResult.Substring(0, 50) + "..." : toolResult;
		Console.WriteLine($"[{DisplayName}] Tool result: {preview}");
		MarkDirty();

		await _compaction.CompactIfNeededAsync(this, cancellationToken);
		await LazySyncIfDueAsync();
	}

	// Forces a compaction pass on the conversation to hoist memories before the conversation is discarded.
	public async Task CompactNowAsync(CancellationToken cancellationToken)
	{
		await _compaction.CompactNowAsync(this, cancellationToken);
		await LazySyncIfDueAsync();
	}

	// Deletes messages from startIndex to endIndex (exclusive), preserving messages before and after.
	// Indices are clamped to protect the first 4 messages (system, instructions, facts, summaries).
	public async Task DeleteRangeAsync(int startIndex, int endIndex, CancellationToken cancellationToken)
	{
		startIndex = Math.Max(startIndex, FirstCompressibleIndex);
		endIndex = Math.Max(endIndex, FirstCompressibleIndex);

		if (startIndex >= Messages.Count || endIndex <= startIndex)
		{
			return;
		}

		endIndex = Math.Min(endIndex, Messages.Count);

		List<ConversationMessage> tailMessages = new List<ConversationMessage>();
		for (int i = endIndex; i < Messages.Count; i++)
		{
			tailMessages.Add(Messages[i]);
		}

		while (Messages.Count > startIndex)
		{
			Messages.RemoveAt(Messages.Count - 1);
		}

		foreach (ConversationMessage msg in tailMessages)
		{
			Messages.Add(msg);
		}

		MarkDirty();
		await LazySyncIfDueAsync();
	}

    public async Task RecordCostAsync(decimal cost, CancellationToken cancellationToken)
    {
        if (cost > 0)
        {
            Ticket? updated = await WorkerSession.ApiClient.AddLlmCostAsync(WorkerSession.TicketHolder.Ticket.Id, cost, cancellationToken);
            WorkerSession.TicketHolder.Update(updated);
        }

        await LazySyncIfDueAsync();
    }

    public decimal GetRemainingBudget()
    {
        decimal maxCost = WorkerSession.TicketHolder.Ticket.MaxCost;
        if (maxCost <= 0)
        {
            return 0;
        }

        decimal currentCost = WorkerSession.TicketHolder.Ticket.LlmCost;
        decimal remaining = maxCost - currentCost;
        return remaining > 0 ? remaining : 0;
    }

    private void MarkCompleted()
    {
        Data.CompletedAt = DateTime.UtcNow.ToString("O");
        MarkDirty();
    }

    // Destroys all messages, memories, and chapter summaries, then rebuilds the conversation from scratch.
    public async Task ResetAsync()
    {
        Messages.Clear();
        _memories.Clear();
        Data.ChapterSummaries.Clear();
        Data.Memories.Clear();

        // Rebuild message 0: latest system prompt.
        string promptKey = Role == LlmRole.Planning ? "planning" : "developer";
        string systemPrompt = WorkerSession.Prompts.TryGetValue(promptKey, out string? latestPrompt)
            ? latestPrompt
            : string.Empty;
        Messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });

        // Rebuild message 1: ticket instructions.
        Ticket ticket = WorkerSession.TicketHolder.Ticket;
        string userPrompt = $"Ticket: {ticket.Title}\nDescription: {ticket.Description}";
        Messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });

        // Rebuild message 2: empty memories.
        Messages.Add(new ConversationMessage { Role = "assistant", Content = _memories.Format() });

        // Rebuild message 3: empty chapter summaries.
        Messages.Add(new ConversationMessage { Role = "assistant", Content = "[Chapter summaries: None yet]" });

        Data.CompletedAt = null;
        Data.IsFinished = false;
        _dirtyTimestamp = DateTime.UtcNow.Ticks;

        await ForceFlushAsync();
        await WorkerSession.HubClient.ResetConversationAsync(Id);
    }

    // Marks the conversation finished, force-syncs to the server, and notifies the hub.
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        MarkCompleted();
        Data.IsFinished = true;
        await ForceFlushAsync();
        await WorkerSession.HubClient.FinishConversationAsync(Id);
    }

    // Force-pushes the current ConversationData snapshot to the server if dirty.
    public async Task ForceFlushAsync()
    {
        if (_dirtyTimestamp == 0)
        {
            return;
        }

        await WorkerSession.HubClient.SyncConversationAsync(Data);
        _dirtyTimestamp = 0;
    }

    // Syncs to the server if dirty for 5+ seconds.
    private async Task LazySyncIfDueAsync()
    {
        if (_dirtyTimestamp == 0)
        {
            return;
        }

        long elapsed = DateTime.UtcNow.Ticks - _dirtyTimestamp;
        if (elapsed >= SyncDelayTicks)
        {
            await WorkerSession.HubClient.SyncConversationAsync(Data);
            _dirtyTimestamp = 0;
        }
    }

    // Records the first time the conversation became dirty. Subsequent calls before a sync are no-ops.
    private void MarkDirty()
    {
        if (_dirtyTimestamp == 0)
        {
            _dirtyTimestamp = DateTime.UtcNow.Ticks;
        }
    }
}

