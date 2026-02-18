using System.ComponentModel;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Stack-Frame Context Management conversation strategy.
//
// MESSAGE STRUCTURE:
//   [0] System prompt - SFCM instructions + role prompt (static, always cached)
//   [1] User goal - The original task (immutable, never rewritten)
//   [2] Memories - Append-only system message populated by FRAME_0 pops
//   [3] System: FRAME_0 marker
//   [4] User: current task instructions (evolves via pop next_steps)
//   [5+] Active frame work
//
// Push creates nested frames with boundary markers. Pop compresses a frame
// into a single tool_result in the parent. FRAME_0 pop appends result to
// memories and rewrites the sentinel to next_steps.
//
public class SfcmConversation : ILlmConversation
{
    private const int MaxDepth = 6;  // any deeper than this and there's a good chance the model is too afraid of solving anything itself so it's recursing
    private const long SyncDelayTicks = TimeSpan.TicksPerSecond * 5;

    private List<Tool>? _allTools;
    private List<Tool>? _popOnlyTools;
    private Tool? _pushTool;
    private Tool? _popToolTemplate;
    private int _toolsBuiltAtDepth = -1;

    private const int MemoriesIndex = 2;

    private readonly ConversationMemories _memories;
    private long _dirtyTimestamp;

    // SFCM frame stack state.
    private readonly List<FrameInfo> _frameStack;

    private ConversationData Data { get; }

    public string Id => Data.Id;
    private string DisplayName => Data.DisplayName;
    public List<ConversationMessage> Messages => Data.Messages;

    public LlmRole Role { get; }

    public ToolContext ToolContext { get; }

    private int Iteration { get; set; }

    private int MaxIterations { get; set; } = 25;

    public bool HasReachedMaxIterations => Iteration >= MaxIterations;

    public SfcmConversation(string systemPrompt, string userGoal, ConversationMemories memories, LlmRole role, ToolContext toolContext, string displayName)
    {
        _memories = memories;

        Data = new ConversationData
        {
            Id = Guid.NewGuid().ToString(),
            DisplayName = displayName,
            StartedAt = DateTime.UtcNow.ToString("O"),
            Memories = memories.Backing,
            ConversationType = "sfcm"
        };

        Role = role;
        ToolContext = toolContext;
        _dirtyTimestamp = DateTime.UtcNow.Ticks;
        _frameStack = new List<FrameInfo>();

        BuildInitialMessages(systemPrompt, userGoal);
    }

    // Restores from server data. Rebuilds frame stack from the message structure.
    public SfcmConversation(ConversationData data, LlmRole role, ToolContext toolContext)
    {
        Data = data;
        _memories = new ConversationMemories(data.Memories);
        Role = role;
        ToolContext = toolContext;
        _dirtyTimestamp = 0;

        _frameStack = new List<FrameInfo>();

        RebuildFrameStack();

        // Refresh system prompt to latest version so prompt edits take effect.
        string promptKey = role == LlmRole.Planning ? "planning" : "developer";
        string rolePrompt = WorkerSession.Prompts[promptKey];
        string sfcmInstructions = WorkerSession.Prompts["sfcm"];
        if (Messages.Count > 0)
        {
            Messages[0] = new ConversationMessage { Role = "system", Content = $"{sfcmInstructions}\n\n{rolePrompt}" };
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

    public async Task AddUserMessageAsync(string content, CancellationToken cancellationToken)
    {
        Messages.Add(new ConversationMessage { Role = "user", Content = content });
        Console.WriteLine($"[{DisplayName}] User: {(content.Length > 50 ? content.Substring(0, 50) + "..." : content)}");
        MarkDirty();
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
        Data.Memories.Clear();
        Data.ChapterSummaries.Clear();

        string promptKey = Role == LlmRole.Planning ? "planning" : "developer";
        string systemPrompt = WorkerSession.Prompts[promptKey];

        Ticket ticket = WorkerSession.TicketHolder.Ticket;
        string userGoal = $"Ticket: {ticket.Title}\nDescription: {ticket.Description}";

        BuildInitialMessages(systemPrompt, userGoal);

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

    // Returns push/pop tools with handlers bound to this instance.
    // Excludes push_context when at maximum frame depth.
    // Rebuilds pop_context description when frame depth changes.
    public IReadOnlyList<Tool> GetAdditionalTools()
    {
        if (_pushTool == null)
        {
            List<Tool> builtTools = new List<Tool>();
            ToolHelper.AddTools(builtTools, this, nameof(PushContextAsync), nameof(PopContextAsync));
            _pushTool = builtTools[0];
            _popToolTemplate = builtTools[1];
        }

        int currentDepth = _frameStack.Count;
        if (_allTools == null || currentDepth != _toolsBuiltAtDepth)
        {
            Tool popTool = BuildPopTool(currentDepth);
            _allTools = new List<Tool> { _pushTool, popTool };
            _popOnlyTools = new List<Tool> { popTool };
            _toolsBuiltAtDepth = currentDepth;
        }

        if (currentDepth >= MaxDepth)
        {
            return _popOnlyTools!;
        }

        return _allTools;
    }

	// Builds a pop_context tool with a description tailored to the current frame depth.
	private Tool BuildPopTool(int currentDepth)
	{
		FrameInfo current = _frameStack[currentDepth - 1];

		string description;
		if (currentDepth <= 1)
		{
			description = """
				Everything from the FRAME_0 marker to now is permanently deleted. The result parameter (if not empty) is appended to the memories block, permanently, and visible in all future frames.
				The next_steps parameter completely replaces the user instructions for FRAME_0, steering what you work on next. The original user goal at message[1] never changes.
				Write result as a concise, self-contained record of what was discovered, accomplished, or learned if it is useful for all future tasks. Write next_steps in great detail to guide your next iteration.
				""";
		}
		else
		{
			string taskPreview = current.Task.Length > 80
				? current.Task.Substring(0, 80) + "..."
				: current.Task;
			string parentFrame = $"FRAME_{currentDepth - 2}";
			description = $"""
				Complete FRAME_{currentDepth - 1} (task: \"{taskPreview}\") and return to {parentFrame}. Everything from the FRAME_{currentDepth - 1} marker to now is deleted.
				This rewrites the conversation at that point to show the original Task followed by your result parameter, which should be in a question/answer format. Immediately following 
				will be the next_steps instructions.  Write result as a concise, self-contained summary of the critical findings: what was discovered, decided, or built. Include details that are useful for future work or answer the Task directly.
				Write next_steps as actionable instructions for what should happen next and why in great detail to guide your next iteration.
				""";
		}

        return new Tool
        {
            Definition = new ToolDefinition
            {
                Type = "function",
                Function = new FunctionDefinition
                {
                    Name = _popToolTemplate!.Definition.Function.Name,
                    Description = description,
                    Parameters = _popToolTemplate.Definition.Function.Parameters
                }
            },
            Handler = _popToolTemplate.Handler
        };
    }

    // When the model produces text without tool calls inside a frame, inject a nudge.
    // At FRAME_0 depth with no deeper frames, treat as completed (return false).
    public async Task<bool> HandleTextResponseAsync(string text, CancellationToken cancellationToken)
    {
        int depth = _frameStack.Count - 1;
        if (depth <= 0)
        {
            return false;
        }

        string nudge = depth > 1
            ? "Continue. When this sub-task is complete, call pop_context with your findings."
            : "Continue.";

        await AddUserMessageAsync(nudge, cancellationToken);
        return true;
    }

    // Pushes a new frame onto the stack.
    private void HandlePush(string task, string details)
    {
        // Task must not contain \n\n so it can be cleanly parsed back out of the user message on reconstitution.
        task = task.Replace("\n\n", "\n");
        int depth = _frameStack.Count;
        string frameId = $"FRAME_{depth}";
        int boundaryIndex = Messages.Count - 1;

        Messages.Add(new ConversationMessage { Role = "system", Content = frameId });

        string userContent = string.IsNullOrWhiteSpace(details) ? task : $"{task}\n\n{details}";
        Messages.Add(new ConversationMessage { Role = "user", Content = userContent });

        _frameStack.Add(new FrameInfo
        {
            Id = frameId,
            Task = task,
            Details = details,
            Depth = depth,
            BoundaryIndex = boundaryIndex,
            StartIndex = Messages.Count - 2
        });
    }

    // Pops the current frame. Depth 1+ truncates and rewrites as a user message.
    // Depth 0 appends result to memories and re-anchors FRAME_0.
    private void HandlePop(string result, string nextSteps)
    {
        if (_frameStack.Count <= 1)
        {
            HandleFrame0Pop(result, nextSteps);
            return;
        }

        FrameInfo current = _frameStack[_frameStack.Count - 1];
        _frameStack.RemoveAt(_frameStack.Count - 1);

        // Truncate everything from the boundary onward (frame marker, user message, all frame work).
        TruncateMessagesTo(current.BoundaryIndex + 1);

        // Remove the push_context tool call from the assistant message so it isn't orphaned.
        RemovePushToolCall(current.BoundaryIndex);

        // Rewrite as a user message: task + result + next steps.
        string userContent = $"{current.Task}\n{result}";
        if (!string.IsNullOrWhiteSpace(nextSteps))
        {
            userContent = $"{userContent}\nNext: {nextSteps}";
        }

        Messages.Add(new ConversationMessage { Role = "user", Content = userContent });
    }

    // Appends result to memories and rewrites FRAME_0 task to next_steps.
    private void HandleFrame0Pop(string result, string nextSteps)
    {
        FrameInfo frame0 = _frameStack[0];

        // Truncate everything from FRAME_0 system message onward.
        TruncateMessagesTo(frame0.StartIndex);

        // Append result to the memories message at index 2.
        string existing = Messages[MemoriesIndex].Content ?? "";
        string updated;
        if (existing == "[Memories: none yet]")
        {
            updated = $"[Memories]\n{result}";
        }
        else
        {
            updated = $"{existing}\n{result}";
        }
        Messages[MemoriesIndex] = new ConversationMessage { Role = "system", Content = updated };

        // Rewrite FRAME_0's task to next_steps for the next iteration.
        if (!string.IsNullOrWhiteSpace(nextSteps))
        {
            frame0.Task = nextSteps;
        }

        // Re-anchor with the (possibly updated) task.
        Messages.Add(new ConversationMessage { Role = "system", Content = "FRAME_0" });
        Messages.Add(new ConversationMessage { Role = "user", Content = frame0.Task });

        frame0.StartIndex = Messages.Count - 2;
    }

    private void TruncateMessagesTo(int count)
    {
        while (Messages.Count > count)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }
    }

    // Removes the push_context tool call from the assistant message at or before the given index.
    // If the assistant message becomes empty (no content, no tool calls), removes it entirely.
    private void RemovePushToolCall(int searchFromIndex)
    {
        for (int i = searchFromIndex; i >= 0; i--)
        {
            ConversationMessage msg = Messages[i];
            if (msg.Role != "assistant" || msg.ToolCalls == null)
            {
                continue;
            }

            ConversationToolCall? pushCall = null;
            foreach (ConversationToolCall tc in msg.ToolCalls)
            {
                if (tc.Function.Name == "push_context")
                {
                    pushCall = tc;
                    break;
                }
            }

            if (pushCall == null)
            {
                continue;
            }

            msg.ToolCalls.Remove(pushCall);

            if (msg.ToolCalls.Count == 0 && string.IsNullOrEmpty(msg.Content))
            {
                Messages.RemoveAt(i);
            }

            return;
        }
    }

    // Rebuilds the frame stack from the message array after reconstitution from server data.
    private void RebuildFrameStack()
    {
        _frameStack.Clear();

        // Scan for FRAME_N markers to rebuild the stack.
        for (int idx = 0; idx < Messages.Count; idx++)
        {
            ConversationMessage msg = Messages[idx];
            if (msg.Role != "system" || msg.Content == null || !msg.Content.StartsWith("FRAME_"))
            {
                continue;
            }

            // Header is just the marker (e.g. "FRAME_0"). Task is in the next user message.
            string frameId = msg.Content.Trim();

            // Strip any legacy "FRAME_N: task" format.
            int colonPos = frameId.IndexOf(':');
            if (colonPos >= 0)
            {
                frameId = frameId.Substring(0, colonPos).Trim();
            }

            string task = "";
            string details = "";
            if (idx + 1 < Messages.Count && Messages[idx + 1].Role == "user")
            {
                string userContent = Messages[idx + 1].Content ?? "";
                int separatorPos = userContent.IndexOf("\n\n");
                if (separatorPos >= 0)
                {
                    task = userContent.Substring(0, separatorPos);
                    details = userContent.Substring(separatorPos + 2);
                }
                else
                {
                    task = userContent;
                }
            }

            int depth = _frameStack.Count;

            // Find the boundary index: the assistant message with push_context before this frame header.
            int boundary = -1;
            if (depth > 0)
            {
                for (int b = idx - 1; b >= 0; b--)
                {
                    ConversationMessage bmsg = Messages[b];
                    if (bmsg.Role == "assistant" && bmsg.ToolCalls != null)
                    {
                        foreach (ConversationToolCall tc in bmsg.ToolCalls)
                        {
                            if (tc.Function.Name == "push_context")
                            {
                                boundary = b;
                                break;
                            }
                        }

                        if (boundary >= 0)
                        {
                            break;
                        }
                    }
                }
            }

            _frameStack.Add(new FrameInfo
            {
                Id = frameId,
                Task = task,
                Details = details,
                Depth = depth,
                BoundaryIndex = boundary,
                StartIndex = idx
            });
        }

        // If no frames found, rebuild from whatever system/user messages exist.
        if (_frameStack.Count == 0 && Messages.Count >= 2)
        {
            string systemPrompt = Messages[0].Content ?? "";
            string goal = Messages[1].Content ?? "";
            Messages.Clear();
            BuildInitialMessages(systemPrompt, goal);
        }
    }

    // Builds the initial message structure: system prompt, user goal, memories, FRAME_0, sentinel.
    private void BuildInitialMessages(string systemPrompt, string userGoal)
    {
        string sfcmInstructions = WorkerSession.Prompts["sfcm"];
        string fullSystemPrompt = $"{sfcmInstructions}\n\n{systemPrompt}";

        Messages.Add(new ConversationMessage { Role = "system", Content = fullSystemPrompt });
        Messages.Add(new ConversationMessage { Role = "user", Content = userGoal });
        Messages.Add(new ConversationMessage { Role = "system", Content = "[Memories: none yet]" });
        Messages.Add(new ConversationMessage { Role = "system", Content = "FRAME_0" });
        Messages.Add(new ConversationMessage { Role = "user", Content = userGoal });

        _frameStack.Clear();
        _frameStack.Add(new FrameInfo
        {
            Id = "FRAME_0",
            Task = userGoal,
            Depth = 0,
            BoundaryIndex = -1,
            StartIndex = 3
        });
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

    // ── Push/pop tool methods ─────────────────────────────────────────────

	[Description("""
		Use push_context to break work into smaller pieces so that intermediate steps that are only temporarily relevant are removed once the effort is completed and the details of exploration, failed attempts, and exact details of how changes are made can be cleanly removed from the conversation.
		Each frame isolates intermediate steps so that only the final answer remains. The task parameter states what to accomplish, or what to investigate. After the pop_context, it will remain as evidence of the work, followed by the result of the work.
		The details parameter carries instructions, file paths, and context — visible only to the agent while doing the work and is permanently deleted on pop.
		""")]
    private async Task<ToolResult> PushContextAsync(
        [Description("A concise one-line statement of the task to accomplish or question to answer. This is the only part of the push that remains after pop.")] string task,
        [Description("Detailed context, instructions, file paths, constraints, and anything the frame needs to do the work. This goes away after pop.")] string details,
        ToolContext context)
    {
        HandlePush(task, details);
        Console.WriteLine($"[{DisplayName}] SFCM push -> FRAME_{_frameStack.Count - 1}: {(task.Length > 60 ? task.Substring(0, 60) + "..." : task)}");
        MarkDirty();
        await LazySyncIfDueAsync();
        return new ToolResult(string.Empty, false, true);
    }

    [Description("See BuildPopTool.")]
    private async Task<ToolResult> PopContextAsync(
        [Description("Critical findings: what was discovered, decided, or built. File paths, function names, key facts.")] string result,
        [Description("Actionable instructions based on these findings in great detail. Describe what should happen next and why.")] string nextSteps,
        ToolContext context)
    {
        int poppedDepth = _frameStack.Count - 1;

        HandlePop(result, nextSteps);
        Console.WriteLine($"[{DisplayName}] SFCM pop FRAME_{poppedDepth}: {(result.Length > 60 ? result.Substring(0, 60) + "..." : result)}");
        MarkDirty();
        await LazySyncIfDueAsync();
        return new ToolResult(string.Empty, false, true);
    }

    // ── Frame stack operations ──────────────────────────────────────────────
    private class FrameInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Task { get; set; } = string.Empty;
        public string Details { get; set; } = string.Empty;
        public int Depth { get; set; }
        public int BoundaryIndex { get; set; }
        public int StartIndex { get; set; }
    }
}
