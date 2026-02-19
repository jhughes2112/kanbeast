using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Centralized static registry of all tools.
// Tool sets are pre-built for each meaningful combination of role and ticket state.
public static class ToolsFactory
{
	private static readonly Dictionary<LlmRole, List<Tool>> Tools = BuildAllToolSets();

	public static List<Tool> GetTools(LlmRole role)
	{
		if (Tools.TryGetValue(role, out List<Tool>? cached))
		{
			return cached;
		}

		throw new InvalidOperationException($"No tool set registered for LlmRole.{role}");
	}

	private static Dictionary<LlmRole, List<Tool>> BuildAllToolSets()
	{
		// Read-only tools shared by developer, inspection agent, and sub-agents.
		List<Tool> readOnlyTools = new List<Tool>();
		ToolHelper.AddTools(readOnlyTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(readOnlyTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(readOnlyTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(readOnlyTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));

		// Planning (Backlog, Done, Failed): task creation + inspection sub-agents. No direct file/shell access.
		List<Tool> planning = new List<Tool>();
		ToolHelper.AddTools(planning, typeof(TicketTools), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync), nameof(TicketTools.DeleteAllTasksAsync), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(planning, typeof(SubAgentTools), nameof(SubAgentTools.StartInspectionAgentAsync));

		// Planning + Active:
		List<Tool> planningActive = new List<Tool>();
		ToolHelper.AddTools(planningActive, typeof(DeveloperTools), nameof(DeveloperTools.StartDeveloperAsync));
		ToolHelper.AddTools(planningActive, typeof(TicketTools), nameof(TicketTools.GetNextWorkItemAsync), nameof(TicketTools.UpdateLlmNotesAsync), nameof(TicketTools.SetTicketStatusAsync), nameof(TicketTools.LogMessageAsync));

		// Developer:
		List<Tool> developer = new List<Tool>(readOnlyTools);
		ToolHelper.AddTools(developer, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developer, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));

		// Developer sub-agent
		List<Tool> devSubagent = new List<Tool>(readOnlyTools);
		ToolHelper.AddTools(devSubagent, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(devSubagent, typeof(FileTools), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(devSubagent, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));

		// Planning sub-agent (inspection): read-only, reports findings back.
		List<Tool> planSubagent = new List<Tool>(readOnlyTools);
		ToolHelper.AddTools(planSubagent, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));

		// Compaction: only needs summarize_history to complete.
		List<Tool> compaction = new List<Tool>();
		ToolHelper.AddTools(compaction, typeof(MemoryTools), nameof(MemoryTools.SummarizeHistoryAsync));

		return new Dictionary<LlmRole, List<Tool>>
		{
			[LlmRole.Planning] = planning,
			[LlmRole.PlanningActive] = planningActive,
			[LlmRole.Developer] = developer,
			[LlmRole.DeveloperSubagent] = devSubagent,
			[LlmRole.PlanningSubagent] = planSubagent,
			[LlmRole.Compaction] = compaction
		};
	}
}
