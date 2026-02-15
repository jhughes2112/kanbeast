using System.ComponentModel;

namespace KanBeast.Worker.Services.Tools;

// Tools for agents to manage persistent memories during their conversation.
// Memories survive compaction and are shared across conversations.
public static class MemoryTools
{
	private static readonly HashSet<string> ValidLabels = new HashSet<string>
	{
		"INVARIANT",
		"CONSTRAINT",
		"DECISION",
		"REFERENCE",
		"OPEN_ITEM"
	};

	[Description("""
		Add a labeled memory to the persistent memories list. Memories survive compaction and are visible across conversations.
		Use this to record important discoveries, decisions, and constraints as you work.
		Labels: INVARIANT (what is), CONSTRAINT (what cannot be), DECISION (what was chosen), REFERENCE (what was done), OPEN_ITEM (what is unresolved).
		Keep entries terse and self-contained.
		""")]
	public static Task<ToolResult> AddMemoryAsync(
		[Description("Label: INVARIANT, CONSTRAINT, DECISION, REFERENCE, or OPEN_ITEM")] string label,
		[Description("Terse, self-contained memory entry")] string content,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(content))
		{
			result = new ToolResult("Error: Content cannot be empty", false);
		}
		else
		{
			string trimmedLabel = label.Trim().ToUpperInvariant();

			if (!ValidLabels.Contains(trimmedLabel))
			{
				result = new ToolResult("Error: Label must be one of: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM", false);
			}
			else
			{
				context.Memories.Add(trimmedLabel, content.Trim());
				context.OnMemoriesChanged?.Invoke();
				result = new ToolResult($"Added [{trimmedLabel}]: {content.Trim()}", false);
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
			result = new ToolResult("Error: Memory text cannot be empty", false);
		}
		else
		{
			string trimmedLabel = label.Trim().ToUpperInvariant();

			if (!ValidLabels.Contains(trimmedLabel))
			{
				result = new ToolResult("Error: Label must be one of: INVARIANT, CONSTRAINT, DECISION, REFERENCE, OPEN_ITEM", false);
			}
			else
			{
				bool removed = context.Memories.Remove(trimmedLabel, memoryToRemove);

				if (removed)
				{
					context.OnMemoriesChanged?.Invoke();
					result = new ToolResult($"Removed [{trimmedLabel}] memory matching: {memoryToRemove}", false);
				}
				else
				{
					result = new ToolResult($"No matching memory found in [{trimmedLabel}] (need >5 character match at start)", false);
				}
			}
		}

		return Task.FromResult(result);
	}
}
