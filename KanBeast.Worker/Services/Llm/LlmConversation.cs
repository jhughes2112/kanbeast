using System.Text;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Compaction-based conversation strategy.
//
// CONVERSATION STRUCTURE:
//   [0] System prompt - Static instructions for the LLM role
//   [1] Chapter summaries - Single assistant message rewritten each compaction
//   [2] User instructions - The task goal; compaction may refine this
//   [3+] Conversation messages - Regular assistant/user/tool exchanges
//
// When context exceeds a threshold (percentage of smallest LLM context window):
//   - Messages 0-2 are preserved (system, summaries, instructions)
//   - Messages 3 to ~80% are summarized via a side-conversation into message[1]
//   - The most recent ~20% are kept intact
//
public class CompactingConversation : ILlmConversation
{
	private const int FirstCompressibleIndex = 3;
	private const int MaxChapterSummaries = 10;
	private const int MinimumCompactionThreshold = 3072;
	private const int CharsPerToken = 4;

	private const long SyncDelayTicks = TimeSpan.TicksPerSecond * 1;
	private long _dirtyTimestamp;

	private ConversationData Data { get; }

	public string Id => Data.Id;
	private string DisplayName => Data.DisplayName;
	public List<ConversationMessage> Messages => Data.Messages;

	private LlmRole _role;

	public LlmRole Role
	{
		get => _role;
		set
		{
			_role = value;

			if (Messages.Count > 0)
			{
				Messages[0] = new ConversationMessage { Role = "system", Content = ResolveSystemPrompt(value) };
				MarkDirty();
			}
		}
	}

	public ToolContext ToolContext { get; }

	private int Iteration { get; set; }

	private int MaxIterations { get; set; } = 25;

	public bool HasReachedMaxIterations => Iteration >= MaxIterations;

	private bool CompactionEnabled => WorkerSession.Prompts.ContainsKey("compaction");

	// Creates a new conversation or reconstitutes from server data.
	// existingData: null to create new, non-null to reconstitute.
	// userPrompt: if non-null, overwrites message[2]; if null, preserves existing.
	// displayName: if non-null, sets the display name; if null, preserves existing.
	// id: explicit conversation ID for new conversations (used for crash recovery). Ignored when reconstituting.
	public CompactingConversation(ConversationData? existingData, LlmRole role, ToolContext toolContext, string? userPrompt, string? displayName, string? id)
	{
		if (existingData != null)
		{
			Data = existingData;
		}
		else
		{
			Data = new ConversationData
			{
				Id = id ?? Guid.NewGuid().ToString(),
				StartedAt = DateTime.UtcNow.ToString("O")
			};
		}

		_role = role;
		ToolContext = toolContext;
		_dirtyTimestamp = existingData == null ? DateTime.UtcNow.Ticks : 0;

		if (displayName != null)
		{
			Data.DisplayName = displayName;
		}

		// Build or rebuild the fixed message slots.
		string systemPrompt = ResolveSystemPrompt(role);

		if (Messages.Count == 0)
		{
			if (string.IsNullOrWhiteSpace(userPrompt))
			{
				throw new ArgumentException("userPrompt is required when creating a new conversation", nameof(userPrompt));
			}

			Messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
			Messages.Add(new ConversationMessage { Role = "assistant", Content = "[Chapter summaries: None yet]" });
			Messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });
		}
		else
		{
			Messages[0] = new ConversationMessage { Role = "system", Content = systemPrompt };

			if (userPrompt != null)
			{
				Messages[2] = new ConversationMessage { Role = "user", Content = userPrompt };
			}
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

	private static string ResolveSystemPrompt(LlmRole role)
	{
		string promptKey = role switch
		{
			LlmRole.Planning => "planning",
			LlmRole.PlanningActive => "planning-active",
			LlmRole.PlanningSubagent => "subagent-planning",
			LlmRole.DeveloperSubagent => "subagent-dev",
			LlmRole.Compaction => "compaction",
			LlmRole.Developer => "developer",
			_ => throw new NotImplementedException()
		};

		return WorkerSession.Prompts[promptKey];
	}

	private void UpdateSummariesMessage()
	{
		if (Messages.Count < FirstCompressibleIndex)
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

		Messages[1] = new ConversationMessage { Role = "assistant", Content = summariesContent };
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
		Data.ActiveModel = modelName;
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
		string userPrompt = Messages.Count > 2 ? Messages[2].Content ?? "" : "";

		if (Role == LlmRole.Planning || Role == LlmRole.PlanningActive)
		{
			userPrompt = WorkerSession.TicketHolder.Ticket.FormatPlanningGoal();
		}

		Messages.Clear();
		Data.ChapterSummaries.Clear();

		string systemPrompt = ResolveSystemPrompt(Role);
		Messages.Add(new ConversationMessage { Role = "system", Content = systemPrompt });
		Messages.Add(new ConversationMessage { Role = "assistant", Content = "[Chapter summaries: None yet]" });
		Messages.Add(new ConversationMessage { Role = "user", Content = userPrompt });

		Data.CompletedAt = null;
		Data.IsFinished = false;
		_dirtyTimestamp = DateTime.UtcNow.Ticks;

		await ForceFlushAsync();
		await WorkerSession.HubClient.ResetConversationAsync(Id);
	}

	public async Task<string?> FinalizeAsync(CancellationToken cancellationToken)
	{
		string? handoffSummary = null;

		if (Messages.Count > FirstCompressibleIndex)
		{
			string logPrefix = !string.IsNullOrWhiteSpace(DisplayName) ? $"{DisplayName} (Handoff)" : "Handoff";
			handoffSummary = await RunCompactionSummaryAsync(FirstCompressibleIndex, Messages.Count, logPrefix, cancellationToken);
		}

		Data.CompletedAt = DateTime.UtcNow.ToString("O");
		Data.IsFinished = true;
		MarkDirty();
		await ForceFlushAsync();
		await WorkerSession.HubClient.FinishConversationAsync(Id);

		return handoffSummary;
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

	// ── Compaction ──────────────────────────────────────────────────────────

	// Runs a compaction LLM conversation over messages[startIndex..endIndex) and returns the summary, or null on failure.
	private async Task<string?> RunCompactionSummaryAsync(int startIndex, int endIndex, string logPrefix, CancellationToken cancellationToken)
	{
		string? llmConfigId = ToolContext.LlmConfigId;

		if (!CompactionEnabled || endIndex <= startIndex || llmConfigId == null)
		{
			return null;
		}

		string originalTask = Messages.Count > 2 ? Messages[2].Content ?? "" : "";
		string historyBlock = BuildHistoryBlock(Messages, startIndex, endIndex);

		string compactionUserPrompt = $"""
			[Original task]
			{originalTask}
			""";

		ToolContext compactionContext = new ToolContext(ToolContext.CurrentTaskId, ToolContext.CurrentSubtaskId, llmConfigId, null);

		CompactingConversation summaryConversation = new CompactingConversation(null, LlmRole.Compaction, compactionContext, compactionUserPrompt, logPrefix, null);

		await summaryConversation.AddUserMessageAsync($"""
			<history>
			{historyBlock}
			</history>

			Read {WorkerSession.WorkDir}/MEMORY.md (create it if missing), update it with any critical project details from this history, then call summarize_history with a factual summary.
			""", cancellationToken);

		LlmResult result = await WorkerSession.LlmProxy.RunToCompletionAsync(
			summaryConversation, llmConfigId, null,
			"Call summarize_history with the chapter summary.",
			3, false, cancellationToken);

		string? summary = null;

		if (result.ExitReason == LlmExitReason.ToolRequestedExit && result.FinalToolCalled == "summarize_history")
		{
			Console.WriteLine($"[{logPrefix}] Compaction complete");
			summary = result.Content;
		}
		else
		{
			string reason = !string.IsNullOrWhiteSpace(result.ErrorMessage) ? result.ErrorMessage : result.ExitReason.ToString();
			Console.WriteLine($"[{logPrefix}] Compaction failed: {reason}");
		}

		return summary;
	}

	private async Task CompactIfNeededAsync(CancellationToken cancellationToken)
	{
		if (!CompactionEnabled)
		{
			return;
		}

		double contextSizePercent = WorkerSession.Compaction.ContextSizePercent;
		int contextLength = WorkerSession.LlmProxy.GetSmallestContextLength();
		int effectiveThreshold;

		if (contextLength > 0 && contextSizePercent > 0)
		{
			effectiveThreshold = Math.Max(MinimumCompactionThreshold, (int)(contextLength * contextSizePercent));
		}
		else
		{
			effectiveThreshold = MinimumCompactionThreshold;
		}

		int estimatedTokens = EstimateTokenCount(Messages);
		if (estimatedTokens <= effectiveThreshold)
		{
			return;
		}

		Console.WriteLine($"[Compaction] ~{estimatedTokens} tokens exceeds {effectiveThreshold} threshold ({contextSizePercent:P0} of {contextLength} smallest context), compacting...");

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

		string logPrefix = !string.IsNullOrWhiteSpace(DisplayName) ? $"{DisplayName} (Compaction)" : "Compaction";
		string? summary = await RunCompactionSummaryAsync(FirstCompressibleIndex, endIndex, logPrefix, cancellationToken);

		if (summary != null)
		{
			if (Data.ChapterSummaries.Count >= MaxChapterSummaries)
			{
				Data.ChapterSummaries.RemoveAt(0);
			}

			Data.ChapterSummaries.Add(summary);
			UpdateSummariesMessage();
			DeleteRange(FirstCompressibleIndex, endIndex);
			Console.WriteLine($"[Compaction] Reduced to ~{EstimateTokenCount(Messages)} tokens, now {Data.ChapterSummaries.Count} chapters");
		}
	}

	// Deletes messages from startIndex to endIndex (exclusive), preserving the fixed header messages.
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

