using System.ComponentModel;
using System.Text;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Compaction-based conversation strategy.
//
// CONVERSATION STRUCTURE:
//   [0] System prompt - Static instructions for the LLM role
//   [1] Initial instructions - The first user message with task details
//   [2] Memories - A dynamically updated message containing accumulated discoveries
//   [3] Chapter summaries - Accumulated summaries from previous compactions
//   [4+] Conversation messages - Regular assistant/user/tool exchanges
//
// When context exceeds a threshold (percentage of smallest LLM context window):
//   - Messages 0-3 are preserved (system, instructions, memories, summaries)
//   - Messages 4 to ~80% are summarized via a side-conversation and added to chapter summaries
//   - The most recent ~20% are kept intact
//   - The compaction LLM can hoist important discoveries into the memories list
//
public class CompactingConversation : ILlmConversation
{
	private const long SyncDelayTicks = TimeSpan.TicksPerSecond * 5;
	private const int FirstCompressibleIndex = 4;
	private const int MaxChapterSummaries = 10;
	private const int MinimumCompactionThreshold = 3072;
	private const int CharsPerToken = 4;

	private static readonly HashSet<string> ValidLabels = new HashSet<string>
	{
		"INVARIANT",
		"CONSTRAINT",
		"DECISION",
		"REFERENCE",
		"OPEN_ITEM"
	};

	private readonly ConversationMemories _memories;
	private readonly string? _compactionPrompt;
	private readonly double _contextSizePercent;
	private long _dirtyTimestamp;

	private ConversationData Data { get; }

	public string Id => Data.Id;
	private string DisplayName => Data.DisplayName;
	public List<ConversationMessage> Messages => Data.Messages;

	public LlmRole Role { get; }

	public ToolContext ToolContext { get; }

	private int Iteration { get; set; }

	private int MaxIterations { get; set; } = 25;

	public bool HasReachedMaxIterations => Iteration >= MaxIterations;

	private bool CompactionEnabled => !string.IsNullOrEmpty(_compactionPrompt);

	// Creates a new conversation. Pass a non-empty compactionPrompt to enable compaction; pass null to disable.
	public CompactingConversation(string systemPrompt, string userPrompt, ConversationMemories memories, LlmRole role, ToolContext toolContext, string? compactionPrompt, double contextSizePercent, string displayName)
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
		_compactionPrompt = compactionPrompt;
		_contextSizePercent = contextSizePercent;
		_dirtyTimestamp = DateTime.UtcNow.Ticks;
		toolContext.OnMemoriesChanged = RefreshMemoriesMessage;

		Messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
		Messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });
		Messages.Add(new ConversationMessage { Role = "assistant", Content = _memories.Format() });
		Messages.Add(new ConversationMessage { Role = "assistant", Content = "[Chapter summaries: None yet]" });
	}

	// Restores from server data. Pass a non-empty compactionPrompt to enable compaction; pass null to disable.
	public CompactingConversation(ConversationData data, LlmRole role, ToolContext toolContext, string? compactionPrompt, double contextSizePercent)
	{
		Data = data;
		_memories = new ConversationMemories(data.Memories);
		Role = role;
		ToolContext = toolContext;
		_compactionPrompt = compactionPrompt;
		_contextSizePercent = contextSizePercent;
		_dirtyTimestamp = 0;
		toolContext.OnMemoriesChanged = RefreshMemoriesMessage;

		// Refresh system prompt to latest version so prompt edits take effect.
		string promptKey = role == LlmRole.Planning ? "planning" : "developer";
		if (Data.Messages.Count > 0)
		{
			Data.Messages[0] = new ConversationMessage { Role = "system", Content = WorkerSession.Prompts[promptKey] };
		}
	}

	public void IncrementIteration()
	{
		Iteration++;
	}

	public void ResetIteration()
	{
		Iteration = 0;
	}

	private void AddMemory(string label, string memory)
	{
		_memories.Add(label, memory);
		UpdateMemoriesMessage();
	}

	private bool RemoveMemory(string label, string memoryToRemove)
	{
		bool removed = _memories.Remove(label, memoryToRemove);
		if (removed)
		{
			UpdateMemoriesMessage();
		}

		return removed;
	}

	private void AddChapterSummary(string summary)
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

	private void RefreshMemoriesMessage()
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
		Messages.Add(new ConversationMessage { Role = "user", Content = content });
		Console.WriteLine($"[{DisplayName}] User: {(content.Length > 50 ? content.Substring(0, 50) + "..." : content)}");
		MarkDirty();

		await CompactIfNeededAsync(cancellationToken);
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

		await CompactIfNeededAsync(cancellationToken);
		await LazySyncIfDueAsync();
	}

	public async Task AddToolMessageAsync(string toolCallId, string toolResult, CancellationToken cancellationToken)
	{
		Messages.Add(new ConversationMessage
		{
			Role = "tool",
			Content = toolResult,
			ToolCallId = toolCallId
		});
		string preview = toolResult.Length > 50 ? toolResult.Substring(0, 50) + "..." : toolResult;
		Console.WriteLine($"[{DisplayName}] Tool result: {preview}");
		MarkDirty();

		await CompactIfNeededAsync(cancellationToken);
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

	public async Task ResetAsync()
	{
		Messages.Clear();
		_memories.Clear();
		Data.ChapterSummaries.Clear();
		Data.Memories.Clear();

		string promptKey = Role == LlmRole.Planning ? "planning" : "developer";
		string systemPrompt = WorkerSession.Prompts[promptKey];
		Messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });

		Ticket ticket = WorkerSession.TicketHolder.Ticket;
		string userPrompt = $"Ticket: {ticket.Title}\nDescription: {ticket.Description}";
		Messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });
		Messages.Add(new ConversationMessage { Role = "assistant", Content = _memories.Format() });
		Messages.Add(new ConversationMessage { Role = "assistant", Content = "[Chapter summaries: None yet]" });

		Data.CompletedAt = null;
		Data.IsFinished = false;
		_dirtyTimestamp = DateTime.UtcNow.Ticks;

		await ForceFlushAsync();
		await WorkerSession.HubClient.ResetConversationAsync(Id);
	}

	public async Task FinalizeAsync(CancellationToken cancellationToken)
	{
		Data.CompletedAt = DateTime.UtcNow.ToString("O");
		Data.IsFinished = true;
		MarkDirty();
		await ForceFlushAsync();
		await WorkerSession.HubClient.FinishConversationAsync(Id);
	}

	public async Task ForceFlushAsync()
	{
		if (_dirtyTimestamp == 0)
		{
			return;
		}

		await WorkerSession.HubClient.SyncConversationAsync(Data);
		_dirtyTimestamp = 0;
	}

	private static readonly List<Tool> AdditionalTools = BuildAdditionalTools();

	public IReadOnlyList<Tool> GetAdditionalTools()
	{
		return AdditionalTools;
	}

	public Task<bool> HandleTextResponseAsync(string text, CancellationToken cancellationToken)
	{
		return Task.FromResult(false);
	}

	// ── Compaction ──────────────────────────────────────────────────────────

	private Task CompactIfNeededAsync(CancellationToken cancellationToken)
	{
		if (!CompactionEnabled)
		{
			return Task.CompletedTask;
		}

		int contextLength = WorkerSession.LlmProxy.GetSmallestContextLength();
		int effectiveThreshold;

		if (contextLength > 0 && _contextSizePercent > 0)
		{
			effectiveThreshold = Math.Max(MinimumCompactionThreshold, (int)(contextLength * _contextSizePercent));
		}
		else
		{
			effectiveThreshold = MinimumCompactionThreshold;
		}

		int estimatedTokens = EstimateTokenCount(Messages);
		if (estimatedTokens <= effectiveThreshold)
		{
			return Task.CompletedTask;
		}

		Console.WriteLine($"[Compaction] ~{estimatedTokens} tokens exceeds {effectiveThreshold} threshold ({_contextSizePercent:P0} of {contextLength} smallest context), compacting...");
		return PerformCompactionAsync(cancellationToken);
	}

	private async Task PerformCompactionAsync(CancellationToken cancellationToken)
	{
		int totalCompressible = Messages.Count - FirstCompressibleIndex;
		if (totalCompressible < 2)
		{
			return;
		}

		int keepRecentCount = Math.Max(1, (int)(totalCompressible * 0.2));
		int endIndex = Messages.Count - keepRecentCount;
		if (endIndex <= FirstCompressibleIndex)
		{
			return;
		}

		string originalTask = Messages.Count > 1 ? Messages[1].Content ?? "" : "";
		string historyBlock = BuildHistoryBlock(Messages, FirstCompressibleIndex, endIndex);

		string userPrompt = $"""
			[Original task]
			{originalTask}
			""";

		ToolContext compactionContext = new ToolContext(ToolContext.CurrentTaskId, ToolContext.CurrentSubtaskId, ToolContext.Memories, null, null);
		string compactLogPrefix = !string.IsNullOrWhiteSpace(DisplayName) ? $"{DisplayName} (Compaction)" : "Compaction";

		CompactingConversation summaryConversation = new CompactingConversation(_compactionPrompt!, userPrompt, _memories, LlmRole.Compaction, compactionContext, null, 0, compactLogPrefix);

		// Memory changes from the compaction conversation should refresh the parent.
		compactionContext.OnMemoriesChanged = RefreshMemoriesMessage;

		await summaryConversation.AddUserMessageAsync($"""
			<history>
			{historyBlock}
			</history>

			First, use add_memory and remove_memory to update the memories with important discoveries, decisions, or state changes from this history.
			Finally, call summarize_history with a concise chapter summary as described in the instructions, which completes the compaction process.
			""", cancellationToken);

		for (int attempt = 0; attempt < 3; attempt++)
		{
			LlmResult result = await WorkerSession.LlmProxy.ContinueAsync(summaryConversation, null, cancellationToken);

			if (result.ExitReason == LlmExitReason.ToolRequestedExit && result.FinalToolCalled == "summarize_history")
			{
				AddChapterSummary(result.Content);
				DeleteRange(FirstCompressibleIndex, endIndex);
				Console.WriteLine($"[Compaction] Reduced to ~{EstimateTokenCount(Messages)} tokens, now {Data.ChapterSummaries.Count} chapters");
				return;
			}
			else if (result.ExitReason == LlmExitReason.Completed || result.ExitReason == LlmExitReason.MaxIterationsReached)
			{
				summaryConversation.ResetIteration();
				await summaryConversation.AddUserMessageAsync("Call summarize_history tool with the summary to complete the compaction process.", cancellationToken);
			}
			else if (result.ExitReason == LlmExitReason.CostExceeded)
			{
				Console.WriteLine("[Compaction] Cost budget exceeded during compaction");
				return;
			}
			else
			{
				Console.WriteLine($"[Compaction] LLM failed during compaction: {result.ErrorMessage}");
				return;
			}
		}

		Console.WriteLine("[Compaction] LLM did not call summarize_history after 3 attempts, skipping compaction");
	}

	// Deletes messages from startIndex to endIndex (exclusive), preserving the first 4 messages.
	private void DeleteRange(int startIndex, int endIndex)
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
	}

	private static string BuildHistoryBlock(List<ConversationMessage> messages, int startIndex, int endIndex)
	{
		StringBuilder builder = new StringBuilder();

		for (int i = startIndex; i < endIndex; i++)
		{
			ConversationMessage message = messages[i];

			if (message.Role == "user")
			{
				builder.Append("user: \"");
				builder.Append(EscapeQuotes(message.Content ?? ""));
				builder.Append("\"\n");
			}
			else if (message.Role == "assistant")
			{
				if (message.ToolCalls != null && message.ToolCalls.Count > 0)
				{
					foreach (ConversationToolCall toolCall in message.ToolCalls)
					{
						builder.Append("assistant: ");
						builder.Append(toolCall.Function.Name);
						builder.Append('(');
						builder.Append(toolCall.Function.Arguments);
						builder.Append(")\n");
					}
				}
				else if (!string.IsNullOrEmpty(message.Content))
				{
					builder.Append("assistant: \"");
					builder.Append(EscapeQuotes(message.Content));
					builder.Append("\"\n");
				}
			}
			else if (message.Role == "tool")
			{
				builder.Append("tool_result: \"");
				builder.Append(EscapeQuotes(message.Content ?? ""));
				builder.Append("\"\n");
			}
		}

		return builder.ToString();
	}

	private static string EscapeQuotes(string text)
	{
		return text.Replace("\"", "\\\"");
	}

	// Estimates token count using ~4 characters per token, plus a fixed tool definition overhead.
	private static int EstimateTokenCount(List<ConversationMessage> messages)
	{
		int chars = 0;

		foreach (ConversationMessage message in messages)
		{
			chars += message.Role.Length + 2;
			chars += message.Content?.Length ?? 12;

			if (message.ToolCalls != null)
			{
				foreach (ConversationToolCall toolCall in message.ToolCalls)
				{
					chars += toolCall.Function.Name.Length;
					chars += toolCall.Function.Arguments?.Length ?? 0;
				}
			}

			chars += 1;
		}

		int toolDefinitionOverhead = 2000;
		return (chars / CharsPerToken) + toolDefinitionOverhead;
	}

	// ── Memory and compaction tool methods ────────────────────────────────

	private static List<Tool> BuildAdditionalTools()
	{
		List<Tool> tools = new List<Tool>();
		ToolHelper.AddTools(tools, typeof(CompactingConversation), nameof(AddMemoryAsync), nameof(RemoveMemoryAsync), nameof(SummarizeHistoryAsync));
		return tools;
	}

	[Description("Add a labeled memory to the persistent memories list. Memories survive compaction and are visible to the agent across the entire conversation.")]
	public static Task<ToolResult> AddMemoryAsync(
		[Description("Label: INVARIANT (what is), CONSTRAINT (what cannot be), DECISION (what was chosen), REFERENCE (what was done), or OPEN_ITEM (what is unresolved)")] string label,
		[Description("Terse, self-contained memory entry")] string content,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(content))
		{
			result = new ToolResult("Error: Content cannot be empty", false, false);
		}
		else
		{
			string trimmedLabel = label.Trim().ToUpperInvariant();

			if (!ValidLabels.Contains(trimmedLabel))
			{
				result = new ToolResult("Error: Label must be one of: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM", false, false);
			}
			else
			{
				context.Memories.Add(trimmedLabel, content.Trim());
				context.OnMemoriesChanged?.Invoke();
				result = new ToolResult($"Added [{trimmedLabel}]: {content.Trim()}", false, false);
			}
		}

		return Task.FromResult(result);
	}

	[Description("Remove a memory that is no longer true, relevant, or has been superseded.")]
	public static Task<ToolResult> RemoveMemoryAsync(
		[Description("Label: INVARIANT, CONSTRAINT, DECISION, REFERENCE, or OPEN_ITEM")] string label,
		[Description("Text to match against existing memories (beginning of the memory entry)")] string memoryToRemove,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(memoryToRemove))
		{
			result = new ToolResult("Error: Memory text cannot be empty", false, false);
		}
		else
		{
			string trimmedLabel = label.Trim().ToUpperInvariant();

			if (!ValidLabels.Contains(trimmedLabel))
			{
				result = new ToolResult("Error: Label must be one of: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM", false, false);
			}
			else
			{
				bool removed = context.Memories.Remove(trimmedLabel, memoryToRemove);

				if (removed)
				{
					context.OnMemoriesChanged?.Invoke();
					result = new ToolResult($"Removed [{trimmedLabel}] memory matching: {memoryToRemove}", false, false);
				}
				else
				{
					result = new ToolResult($"No matching memory found in [{trimmedLabel}] (need >5 character match at start)", false, false);
				}
			}
		}

		return Task.FromResult(result);
	}

	[Description("Provide the final summary of the history block and complete the compaction process.")]
	public static Task<ToolResult> SummarizeHistoryAsync(
		[Description("Concise summary of the work done, as it pertains to solving the original task")] string summary,
		ToolContext context)
	{
		ToolResult result = new ToolResult(summary.Trim(), true, false);
		return Task.FromResult(result);
	}

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

	private void MarkDirty()
	{
		if (_dirtyTimestamp == 0)
		{
			_dirtyTimestamp = DateTime.UtcNow.Ticks;
		}
	}
}

