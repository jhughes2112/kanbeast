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
        // Tools are added explicitly to each role below. Do not use a shared readOnlyTools list.

		// Planning (Backlog, Done, Failed): task creation + inspection sub-agents + read_file for MEMORY.md.
		List<Tool> planning = new List<Tool>();
		ToolHelper.AddTools(planning, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(planning, typeof(TicketTools), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync), nameof(TicketTools.DeleteAllTasksAsync), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(planning, typeof(SubAgentTools), nameof(SubAgentTools.StartInspectionAgentAsync));

		// Planning + Active: dispatches developers, verifies results, manages LLM notes + read_file for MEMORY.md.
		List<Tool> planningActive = new List<Tool>();
		ToolHelper.AddTools(planningActive, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(planningActive, typeof(DeveloperTools), nameof(DeveloperTools.StartDeveloperAsync));
		ToolHelper.AddTools(planningActive, typeof(SubAgentTools), nameof(SubAgentTools.StartInspectionAgentAsync));
		ToolHelper.AddTools(planningActive, typeof(TicketTools), nameof(TicketTools.GetNextWorkItemAsync), nameof(TicketTools.UpdateLlmNotesAsync), nameof(TicketTools.SetTicketStatusAsync), nameof(TicketTools.LogMessageAsync));

		// Developer: reads files and searches to understand context, delegates all edits/commands to sub-agents.
		List<Tool> developer = new List<Tool>();
		ToolHelper.AddTools(developer, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(developer, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(developer, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(developer, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developer, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));

        // Developer sub-agent: has explicit read/search/web tools plus shell and edit capabilities.
		List<Tool> devSubagent = new List<Tool>();
		ToolHelper.AddTools(devSubagent, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(devSubagent, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(devSubagent, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(devSubagent, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(devSubagent, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(devSubagent, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));

		// Planning sub-agent (inspection): explicit read/search/web and shell, plus write_file for MEMORY.md.
		List<Tool> planSubagent = new List<Tool>();
		ToolHelper.AddTools(planSubagent, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(planSubagent, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync));
		ToolHelper.AddTools(planSubagent, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(planSubagent, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(planSubagent, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));

		// Compaction: file access to read/update MEMORY.md, plus summarize_history to complete.
		List<Tool> compaction = new List<Tool>();
		ToolHelper.AddTools(compaction, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.WriteFileAsync));
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
