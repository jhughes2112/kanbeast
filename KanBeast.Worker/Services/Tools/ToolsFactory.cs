using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Centralized static registry of all tools, keyed by role.
public static class ToolsFactory
{
	private static readonly Dictionary<LlmRole, List<Tool>> ToolsByRole = BuildToolsByRole();

	public static List<Tool> GetTools(LlmRole role)
	{
		if (ToolsByRole.TryGetValue(role, out List<Tool>? tools))
		{
			return tools;
		}

		return new List<Tool>();
	}

	private static Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
	{
		List<Tool> planningTools = new List<Tool>();
		ToolHelper.AddTools(planningTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(planningTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(planningTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(planningTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(planningTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(planningTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync),
			nameof(TicketTools.PlanningCompleteAsync),
			nameof(TicketTools.DeleteAllTasksAsync));

		List<Tool> qaTools = new List<Tool>();
		ToolHelper.AddTools(qaTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(qaTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(qaTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(qaTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(qaTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(qaTools, typeof(QATools),
			nameof(QATools.ApproveSubtaskAsync),
			nameof(QATools.RejectSubtaskAsync));

		List<Tool> developerTools = new List<Tool>();
		ToolHelper.AddTools(developerTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(developerTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(developerTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(developerTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(developerTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(developerTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developerTools, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));

		// Sub-agent tools: same as developer minus fork_agent, create_task, create_subtask.
		List<Tool> subAgentTools = new List<Tool>();
		ToolHelper.AddTools(subAgentTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(subAgentTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(subAgentTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(subAgentTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(subAgentTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(subAgentTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(subAgentTools, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));

		List<Tool> compactionTools = new List<Tool>();
		ToolHelper.AddTools(compactionTools, typeof(CompactionSummarizer),
			nameof(CompactionSummarizer.AddMemoryAsync),
			nameof(CompactionSummarizer.RemoveMemoryAsync),
			nameof(CompactionSummarizer.SummarizeHistoryAsync));

		Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
		{
			[LlmRole.Planning] = planningTools,
			[LlmRole.QA] = qaTools,
			[LlmRole.Developer] = developerTools,
			[LlmRole.Compaction] = compactionTools,
			[LlmRole.SubAgent] = subAgentTools
		};

		return result;
	}
}
