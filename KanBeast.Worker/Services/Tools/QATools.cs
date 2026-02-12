using System.ComponentModel;

namespace KanBeast.Worker.Services.Tools;

// QA review tools used by the QA agent to approve or reject subtask work.
public static class QATools
{
	[Description("Approve the developer's work on this subtask. Call this when the work meets the acceptance criteria.")]
	public static Task<ToolResult> ApproveSubtaskAsync(
		[Description("Summary of what was verified")] string notes,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(notes))
		{
			result = new ToolResult("Error: Notes are required", false);
		}
		else
		{
			result = new ToolResult(notes, true);
		}

		return Task.FromResult(result);
	}

	[Description("Reject the developer's work on this subtask. The developer will retry with your feedback.")]
	public static Task<ToolResult> RejectSubtaskAsync(
		[Description("Specific feedback on what needs to be fixed")] string feedback,
		ToolContext context)
	{
		ToolResult result;

		if (string.IsNullOrWhiteSpace(feedback))
		{
			result = new ToolResult("Error: Feedback is required", false);
		}
		else
		{
			result = new ToolResult(feedback, true);
		}

		return Task.FromResult(result);
	}
}
