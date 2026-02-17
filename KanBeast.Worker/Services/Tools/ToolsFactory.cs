using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Centralized static registry of all tools.
// Tool sets are pre-built for each meaningful combination of role and ticket state.
public static class ToolsFactory
{
	private enum ToolSet
	{
		PlanningBacklog,
		PlanningActive,
		PlanningOther,
		Developer,
		SubAgent,
		Compaction
	}

	private static readonly Dictionary<ToolSet, List<Tool>> Tools = BuildAllToolSets();

	public static List<Tool> GetTools(LlmRole role, TicketStatus status)
	{
		ToolSet key = role switch
		{
			LlmRole.Planning when status == TicketStatus.Backlog => ToolSet.PlanningBacklog,
			LlmRole.Planning when status == TicketStatus.Active => ToolSet.PlanningActive,
			LlmRole.Planning => ToolSet.PlanningOther,
			LlmRole.Developer => ToolSet.Developer,
			LlmRole.SubAgent => ToolSet.SubAgent,
			LlmRole.Compaction => ToolSet.Compaction,
			_ => ToolSet.PlanningOther
		};

		if (Tools.TryGetValue(key, out List<Tool>? tools))
		{
			return tools;
		}

		return new List<Tool>();
	}

	private static Dictionary<ToolSet, List<Tool>> BuildAllToolSets()
	{
		// Shared tool groups built once and reused across sets.
		List<Tool> commonPlanningTools = new List<Tool>();
		ToolHelper.AddTools(commonPlanningTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(commonPlanningTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(commonPlanningTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(commonPlanningTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(commonPlanningTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(commonPlanningTools, typeof(MemoryTools), nameof(MemoryTools.AddMemoryAsync), nameof(MemoryTools.RemoveMemoryAsync));

		// Planning + Backlog: common + task creation tools.
		List<Tool> planningBacklog = new List<Tool>(commonPlanningTools);
		ToolHelper.AddTools(planningBacklog, typeof(TicketTools), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync), nameof(TicketTools.DeleteAllTasksAsync));

		// Planning + Active: common + developer orchestration tools.
		List<Tool> planningActive = new List<Tool>(commonPlanningTools);
		ToolHelper.AddTools(planningActive, typeof(DeveloperTools), nameof(DeveloperTools.StartDeveloperAsync));
		ToolHelper.AddTools(planningActive, typeof(TicketTools), nameof(TicketTools.GetNextWorkItemAsync), nameof(TicketTools.UpdateLlmNotesAsync));

		// Developer: full capabilities including sub-agents.
		List<Tool> developer = new List<Tool>();
		ToolHelper.AddTools(developer, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(developer, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(developer, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(developer, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(developer, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(developer, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developer, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));
		ToolHelper.AddTools(developer, typeof(MemoryTools), nameof(MemoryTools.AddMemoryAsync), nameof(MemoryTools.RemoveMemoryAsync));

		// Sub-agent: same as developer minus sub-agent spawning.
		List<Tool> subAgent = new List<Tool>();
		ToolHelper.AddTools(subAgent, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(subAgent, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(subAgent, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(subAgent, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(subAgent, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(subAgent, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(subAgent, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));
		ToolHelper.AddTools(subAgent, typeof(MemoryTools), nameof(MemoryTools.AddMemoryAsync), nameof(MemoryTools.RemoveMemoryAsync));

		// Compaction: summarization tools only.
		List<Tool> compaction = new List<Tool>();
		ToolHelper.AddTools(compaction, typeof(CompactionSummarizer),
			nameof(CompactionSummarizer.AddMemoryAsync),
			nameof(CompactionSummarizer.RemoveMemoryAsync),
			nameof(CompactionSummarizer.SummarizeHistoryAsync));

		return new Dictionary<ToolSet, List<Tool>>
		{
			[ToolSet.PlanningBacklog] = planningBacklog,
			[ToolSet.PlanningActive] = planningActive,
			[ToolSet.PlanningOther] = commonPlanningTools,
			[ToolSet.Developer] = developer,
			[ToolSet.SubAgent] = subAgent,
			[ToolSet.Compaction] = compaction
		};
	}
}
