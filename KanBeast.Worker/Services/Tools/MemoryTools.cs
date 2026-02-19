using System.ComponentModel;

namespace KanBeast.Worker.Services.Tools;

// Tools used during conversation compaction.
public static class MemoryTools
{
	[Description("Provide the final summary of the history block and complete the compaction process.")]
	public static Task<ToolResult> SummarizeHistoryAsync(
		[Description("Concise summary of the work done, as it pertains to solving the original task")] string summary,
		ToolContext context)
	{
		ToolResult result = new ToolResult(summary.Trim(), true, false);
		return Task.FromResult(result);
	}
}
