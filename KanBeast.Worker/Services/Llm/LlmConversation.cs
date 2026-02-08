using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static int s_conversationIndex;

    private readonly bool _jsonLogging;
    private readonly LlmMemories _memories;
    private readonly List<string> _chapterSummaries;
    private string _logPath;
    private int _rewriteCount;

    public LlmConversation(string model, string systemPrompt, string userPrompt, LlmMemories memories, bool jsonLogging, string logDirectory, string logPrefix)
    {
        Model = model;
        StartedAt = DateTime.UtcNow.ToString("O");
        CompletedAt = string.Empty;
        Messages = new List<ChatMessage>();
        _memories = memories;
        _chapterSummaries = new List<string>();
        _jsonLogging = jsonLogging;
        _rewriteCount = 0;

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
        ChatMessage systemMessage = new ChatMessage { Role = "system", Content = systemPrompt };
        Messages.Add(systemMessage);

        // Message 1: Initial instructions
        ChatMessage instructionsMessage = new ChatMessage { Role = "user", Content = userPrompt };
        Messages.Add(instructionsMessage);

        // Message 2: Memories (reflects current state of shared memories)
        ChatMessage memoriesMessage = new ChatMessage { Role = "user", Content = _memories.FormatForPrompt() };
        Messages.Add(memoriesMessage);

        // Message 3: Chapter summaries (empty initially)
        ChatMessage summariesMessage = new ChatMessage { Role = "user", Content = "[Chapter summaries: None yet]" };
        Messages.Add(summariesMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            string? directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string header = $"# LLM Conversation Log\n**Model:** {Model}\n**Started:** {StartedAt}\n---\n\n";
            Console.Write(header);
            File.WriteAllText(_logPath, header);
        }
        else
        {
            string header = $"# LLM Conversation Log\n**Model:** {Model}\n**Started:** {StartedAt}\n---\n\n";
            Console.Write(header);
        }
        AppendMessageToLog(systemMessage);
        AppendMessageToLog(instructionsMessage);
        AppendMessageToLog(memoriesMessage);
        AppendMessageToLog(summariesMessage);
    }

    // Index of the first compressible message (after system, instructions, memories, and summaries)
    public const int FirstCompressibleIndex = 4;

    [JsonPropertyName("model")]
    public string Model { get; private set; }

    [JsonPropertyName("started_at")]
    public string StartedAt { get; }

    [JsonPropertyName("completed_at")]
    public string CompletedAt { get; private set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; }

	[JsonIgnore]
	public LlmMemories Memories => _memories;

	[JsonIgnore]
	public IReadOnlyList<string> ChapterSummaries => _chapterSummaries;

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

	public void SetModel(string model)
	{
		Model = model;
	}

	public void AddMemory(string memory)
	{
		_memories.Add(memory);
		UpdateMemoriesMessage();
	}

	public bool RemoveMemory(string memoryToRemove)
	{
		bool removed = _memories.Remove(memoryToRemove);
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

        // Remove oldest summary if at max capacity
        if (_chapterSummaries.Count >= MaxChapterSummaries)
        {
            _chapterSummaries.RemoveAt(0);
        }

        _chapterSummaries.Add(summary);
        UpdateSummariesMessage();
    }

    private void UpdateMemoriesMessage()
    {
        if (Messages.Count < 4)
        {
            return;
        }

        string memoriesContent = _memories.FormatForPrompt();

        Messages[2] = new ChatMessage { Role = "user", Content = memoriesContent };
    }

    private void UpdateSummariesMessage()
    {
        if (Messages.Count < 4)
        {
            return;
        }

        string summariesContent;
        if (_chapterSummaries.Count == 0)
        {
            summariesContent = "[Chapter summaries: None yet]";
        }
        else
        {
            summariesContent = "[Chapter summaries]\n" + string.Join("\n\n", _chapterSummaries.Select((s, i) => $"### Chapter {i + 1}\n{s}"));
        }

        Messages[3] = new ChatMessage { Role = "user", Content = summariesContent };
    }

    public void AddUserMessage(string content)
    {
        ChatMessage message = new ChatMessage
        {
            Role = "user",
            Content = content
        };
        Messages.Add(message);
        AppendMessageToLog(message);
    }

    public void AddAssistantMessage(ChatMessage message)
    {
        Messages.Add(message);
        AppendMessageToLog(message);
    }

    public void AddToolMessage(string toolCallId, string toolResult)
    {
        ChatMessage message = new ChatMessage
        {
            Role = "tool",
            Content = toolResult,
            ToolCallId = toolCallId
        };
        Messages.Add(message);
        AppendMessageToLog(message);
    }

    // Deletes messages from startIndex to endIndex (exclusive), preserving messages before and after.
    // Indices are clamped to protect the first 4 messages (system, instructions, facts, summaries).
    // Closes current log and starts a new one with incremented rewrite count.
    public async Task DeleteRangeAsync(int startIndex, int endIndex, CancellationToken cancellationToken)
    {
        // Clamp to protect first 4 messages
        startIndex = Math.Max(startIndex, FirstCompressibleIndex);
        endIndex = Math.Max(endIndex, FirstCompressibleIndex);

        if (startIndex >= Messages.Count || endIndex <= startIndex)
        {
            return;
        }

        endIndex = Math.Min(endIndex, Messages.Count);

        // Write pre-delete JSON log if enabled
        if (_jsonLogging && !string.IsNullOrWhiteSpace(_logPath))
        {
            await WriteLogAsync($"-pre-compact-{_rewriteCount}", cancellationToken);
        }

        // Close current log
        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            string closeMessage = $"\n---\n**Compacted at:** {DateTime.UtcNow:O}\n";
            await File.AppendAllTextAsync(_logPath, closeMessage, cancellationToken);
        }

        // Collect messages to keep after the deleted range
        List<ChatMessage> tailMessages = new List<ChatMessage>();
        for (int i = endIndex; i < Messages.Count; i++)
        {
            tailMessages.Add(Messages[i]);
        }

        // Remove messages from startIndex onwards
        while (Messages.Count > startIndex)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        // Add back the tail messages
        foreach (ChatMessage msg in tailMessages)
        {
            Messages.Add(msg);
        }

        // Start new log file
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

            string header = $"# LLM Conversation Log (Compacted {_rewriteCount})\n**Model:** {Model}\n**Started:** {StartedAt}\n---\n\n";
            Console.Write(header);
            await File.WriteAllTextAsync(_logPath, header, cancellationToken);

            foreach (ChatMessage msg in Messages)
            {
                await AppendMessageToLogAsync(msg, cancellationToken);
            }
        }
    }

    public void MarkCompleted()
    {
        CompletedAt = DateTime.UtcNow.ToString("O");

        string completionMessage = $"\n---\n**Completed:** {CompletedAt}\n";
        Console.Write(completionMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            File.AppendAllText(_logPath, completionMessage);
        }
    }

    public async Task FinalizeAsync(CancellationToken cancellationToken)
    {
        MarkCompleted();
        if (_jsonLogging)
        {
            await WriteLogAsync("-complete", cancellationToken);
        }
    }

    private void AppendMessageToLog(ChatMessage message)
    {
        string formattedMessage = FormatMessage(message);
        Console.Write(formattedMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            File.AppendAllText(_logPath, formattedMessage);
        }
    }

    private async Task AppendMessageToLogAsync(ChatMessage message, CancellationToken cancellationToken)
    {
        string formattedMessage = FormatMessage(message);
        Console.Write(formattedMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            await File.AppendAllTextAsync(_logPath, formattedMessage, cancellationToken);
        }
    }

    private static string FormatMessage(ChatMessage message)
    {
        if (message.Role == "system")
        {
            return $"## System:\n{message.Content ?? string.Empty}\n\n";
        }

        if (message.Role == "user")
        {
            return $"## User:\n{message.Content ?? string.Empty}\n\n";
        }

        if (message.Role == "assistant")
        {
            string result = "## Assistant:\n";
            if (!string.IsNullOrEmpty(message.Content))
            {
                result += $"{message.Content}\n\n";
            }

            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                foreach (ToolCallMessage toolCall in message.ToolCalls)
                {
                    result += $"**Tool call:** {toolCall.Function.Name} {toolCall.Function.Arguments}\n";
                }
                result += "\n";
            }

            return result;
        }

        if (message.Role == "tool")
        {
            return $"**Tool result:**\n```\n{message.Content ?? string.Empty}\n```\n\n";
        }

        return string.Empty;
    }

    public async Task WriteLogAsync(string suffix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            return;
        }

        string jsonPath = Path.ChangeExtension(_logPath, null) + suffix + ".json";

        string? directory = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string content = JsonSerializer.Serialize(this, JsonOptions);
        await File.WriteAllTextAsync(jsonPath, content, cancellationToken);
    }
}
