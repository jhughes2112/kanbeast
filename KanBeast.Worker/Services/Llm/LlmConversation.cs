using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Holds the content and logging state for a single LLM conversation.
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
public class LlmConversation
{
    private static int s_conversationIndex;

    private readonly ConversationMemories _memories;
    private readonly ICompaction _compaction;
    private readonly string _logDirectory;
    private readonly string _logPrefix;
    private string _logPath;
    private int _rewriteCount;
    private bool _dirty;

    // The serializable core — this is what gets synced to the server.
    public ConversationData Data { get; }

    // Convenience accessors that delegate to Data.
    public string Id => Data.Id;
    public string DisplayName => Data.DisplayName;
    public List<ConversationMessage> Messages => Data.Messages;

    public LlmConversation(string systemPrompt, string userPrompt, ConversationMemories memories, LlmRole role, ToolContext toolContext, ICompaction compaction, string logDirectory, string logPrefix, string displayName)
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
        _logDirectory = logDirectory;
        _logPrefix = logPrefix;
        _rewriteCount = 0;
        _dirty = true;

        if (!string.IsNullOrWhiteSpace(logDirectory) && !string.IsNullOrWhiteSpace(logPrefix))
        {
            int index = Interlocked.Increment(ref s_conversationIndex);
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _logPath = Path.Combine(logDirectory, $"{logPrefix}-{timestamp}-{index:D3}.log");
        }
        else
        {
            _logPath = string.Empty;
        }

        // Message 0: System prompt
        ConversationMessage systemMessage = new ConversationMessage { Role = "system", Content = systemPrompt };
        Messages.Add(systemMessage);

        // Message 1: Initial instructions
        ConversationMessage instructionsMessage = new ConversationMessage { Role = "user", Content = userPrompt };
        Messages.Add(instructionsMessage);

        // Message 2: Memories (reflects current state of shared memories)
        ConversationMessage memoriesMessage = new ConversationMessage { Role = "user", Content = _memories.Format() };
        Messages.Add(memoriesMessage);

        // Message 3: Chapter summaries (empty initially)
        ConversationMessage summariesMessage = new ConversationMessage { Role = "user", Content = "[Chapter summaries: None yet]" };
        Messages.Add(summariesMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            string? directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string header = $"# LLM Conversation Log\n**Started:** {Data.StartedAt}\n---\n\n";
            Console.Write(header);
            File.WriteAllText(_logPath, header);
        }
        else
        {
            string header = $"# LLM Conversation Log\n**Started:** {Data.StartedAt}\n---\n\n";
            Console.Write(header);
        }
        AppendMessageToLog(systemMessage, null);
        AppendMessageToLog(instructionsMessage, null);
        AppendMessageToLog(memoriesMessage, null);
        AppendMessageToLog(summariesMessage, null);
    }

    // Restores a conversation from server data. Memories are reconstituted from Data.Memories.
    public LlmConversation(ConversationData data, LlmRole role, ToolContext toolContext, ICompaction compaction, string logDirectory, string logPrefix)
    {
        Data = data;
        _memories = new ConversationMemories(data.Memories);
        Role = role;
        ToolContext = toolContext;
        _compaction = compaction;
        _logDirectory = logDirectory;
        _logPrefix = logPrefix;
        _rewriteCount = 0;
        _dirty = false;
        _logPath = string.Empty;
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
	public string LogDirectory => _logDirectory;

	[JsonIgnore]
	public string LogPrefix => _logPrefix;

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

    private void UpdateMemoriesMessage()
    {
        if (Messages.Count < 4)
        {
            return;
        }

        Messages[2] = new ConversationMessage { Role = "user", Content = _memories.Format() };
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

        Messages[3] = new ConversationMessage { Role = "user", Content = summariesContent };
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
		AppendMessageToLog(message, null);
		MarkDirty();

		await _compaction.CompactIfNeededAsync(this, cancellationToken);
	}

	public async Task AddAssistantMessageAsync(ConversationMessage message, string modelName, CancellationToken cancellationToken)
	{
		Messages.Add(message);
		AppendMessageToLog(message, modelName);
		MarkDirty();

		await _compaction.CompactIfNeededAsync(this, cancellationToken);
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
		AppendMessageToLog(message, null);
		MarkDirty();

		await _compaction.CompactIfNeededAsync(this, cancellationToken);
	}

	// Forces a compaction pass on the conversation to hoist memories before the conversation is discarded.
	public async Task CompactNowAsync(CancellationToken cancellationToken)
	{
		await _compaction.CompactNowAsync(this, cancellationToken);
	}

	// Deletes messages from startIndex to endIndex (exclusive), preserving messages before and after.
    // Indices are clamped to protect the first 4 messages (system, instructions, facts, summaries).
    // Closes current log and starts a new one with incremented rewrite count.
    public async Task DeleteRangeAsync(int startIndex, int endIndex, CancellationToken cancellationToken)
    {
        startIndex = Math.Max(startIndex, FirstCompressibleIndex);
        endIndex = Math.Max(endIndex, FirstCompressibleIndex);

        if (startIndex >= Messages.Count || endIndex <= startIndex)
        {
            return;
        }

        endIndex = Math.Min(endIndex, Messages.Count);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            string closeMessage = $"\n---\n**Compacted at:** {DateTime.UtcNow:O}\n";
            await File.AppendAllTextAsync(_logPath, closeMessage, cancellationToken);
        }

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

        _rewriteCount++;
        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            string basePath = Path.ChangeExtension(_logPath, null);
            _logPath = $"{basePath}-c{_rewriteCount}.log";

            string? directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string header = $"# LLM Conversation Log (Compacted {_rewriteCount})\n**Started:** {Data.StartedAt}\n---\n\n";
            Console.Write(header);
            await File.WriteAllTextAsync(_logPath, header, cancellationToken);

            foreach (ConversationMessage msg in Messages)
            {
                await AppendMessageToLogAsync(msg, null, cancellationToken);
            }
        }

        MarkDirty();
    }

    public async Task RecordCostAsync(decimal cost, CancellationToken cancellationToken)
    {
        if (cost > 0)
        {
            Ticket? updated = await WorkerSession.ApiClient.AddLlmCostAsync(WorkerSession.TicketHolder.Ticket.Id, cost, cancellationToken);
            WorkerSession.TicketHolder.Update(updated);
        }
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

    public void MarkCompleted()
    {
        Data.CompletedAt = DateTime.UtcNow.ToString("O");

        string completionMessage = $"\n---\n**Completed:** {Data.CompletedAt}\n";
        Console.Write(completionMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            File.AppendAllText(_logPath, completionMessage);
        }
    }

    // Marks the conversation finished, syncs to the server, and notifies the hub.
    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        MarkCompleted();
        Data.IsFinished = true;
        await SyncToServerAsync();
        await WorkerSession.HubClient.FinishConversationAsync(Id);
    }

    // Pushes the current ConversationData snapshot to the server if dirty.
    public async Task SyncToServerAsync()
    {
        if (!_dirty)
        {
            return;
        }

        await WorkerSession.HubClient.SyncConversationAsync(Data);
        _dirty = false;
    }

    private void MarkDirty()
    {
        _dirty = true;
    }

    private void AppendMessageToLog(ConversationMessage message, string? modelName)
    {
        string formattedMessage = FormatMessage(message, modelName);
        Console.Write(formattedMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            File.AppendAllText(_logPath, formattedMessage);
        }
    }

    private async Task AppendMessageToLogAsync(ConversationMessage message, string? modelName, CancellationToken cancellationToken)
    {
        string formattedMessage = FormatMessage(message, modelName);
        Console.Write(formattedMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            await File.AppendAllTextAsync(_logPath, formattedMessage, cancellationToken);
        }
    }

    private static string FormatMessage(ConversationMessage message, string? modelName)
    {
        if (message.Role == "system")
        {
            return $"system: {message.Content ?? string.Empty}\n";
        }

        if (message.Role == "user")
        {
            return $"user: {message.Content ?? string.Empty}\n";
        }

        if (message.Role == "assistant")
        {
            string prefix = !string.IsNullOrWhiteSpace(modelName) ? $"[{modelName}] " : string.Empty;
            string result = $"{prefix}assistant: ";

            if (!string.IsNullOrEmpty(message.Content))
            {
                result += $"{message.Content}\n";
            }

            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                foreach (ConversationToolCall toolCall in message.ToolCalls)
                {
                    result += $"  tool call: {toolCall.Function.Name} {toolCall.Function.Arguments}\n";
                }
            }

            return result;
        }

        if (message.Role == "tool")
        {
            return $"  tool result: {message.Content ?? string.Empty}\n";
        }

        return string.Empty;
    }
}

