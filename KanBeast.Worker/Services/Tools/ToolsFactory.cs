using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Centralized static registry of all tools, keyed by role.
// Planning tools are composed at runtime based on ticket state.
public static class ToolsFactory
{
	private static readonly Dictionary<LlmRole, List<Tool>> ToolsByRole = BuildToolsByRole();
	private static readonly List<Tool> BacklogOnlyPlanningTools = BuildBacklogOnlyPlanningTools();
	private static readonly List<Tool> ActiveOnlyPlanningTools = BuildActiveOnlyPlanningTools();

	public static List<Tool> GetTools(LlmRole role)
	{
		if (!ToolsByRole.TryGetValue(role, out List<Tool>? tools))
		{
			return new List<Tool>();
		}

		if (role == LlmRole.Planning)
		{
			List<Tool> composed = new List<Tool>(tools);
			TicketStatus status = WorkerSession.TicketHolder.Ticket.Status;

			if (status == TicketStatus.Backlog)
			{
				composed.AddRange(BacklogOnlyPlanningTools);
			}
			else if (status == TicketStatus.Active)
			{
				composed.AddRange(ActiveOnlyPlanningTools);
			}

			return composed;
		}

		return tools;
	}

	private static List<Tool> BuildBacklogOnlyPlanningTools()
	{
		List<Tool> tools = new List<Tool>();
		ToolHelper.AddTools(tools, typeof(TicketTools), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync), nameof(TicketTools.DeleteAllTasksAsync));
		return tools;
	}

	private static List<Tool> BuildActiveOnlyPlanningTools()
	{
		List<Tool> tools = new List<Tool>();
		ToolHelper.AddTools(tools, typeof(DeveloperTools), nameof(DeveloperTools.StartDeveloperAsync));
		ToolHelper.AddTools(tools, typeof(TicketTools), nameof(TicketTools.GetNextWorkItemAsync), nameof(TicketTools.UpdateLlmNotesAsync));
		return tools;
	}

	private static Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
	{
		List<Tool> planningTools = new List<Tool>();
		ToolHelper.AddTools(planningTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(planningTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(planningTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(planningTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(planningTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(planningTools, typeof(MemoryTools), nameof(MemoryTools.AddMemoryAsync), nameof(MemoryTools.RemoveMemoryAsync));

		List<Tool> developerTools = new List<Tool>();
		ToolHelper.AddTools(developerTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(developerTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(developerTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(developerTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(developerTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(developerTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developerTools, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));
		ToolHelper.AddTools(developerTools, typeof(MemoryTools), nameof(MemoryTools.AddMemoryAsync), nameof(MemoryTools.RemoveMemoryAsync));

		// Sub-agent tools: same as developer minus sub-agent spawning.
		List<Tool> subAgentTools = new List<Tool>();
		ToolHelper.AddTools(subAgentTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(subAgentTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(subAgentTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(subAgentTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(subAgentTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(subAgentTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(subAgentTools, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));
		ToolHelper.AddTools(subAgentTools, typeof(MemoryTools), nameof(MemoryTools.AddMemoryAsync), nameof(MemoryTools.RemoveMemoryAsync));

		List<Tool> compactionTools = new List<Tool>();
		ToolHelper.AddTools(compactionTools, typeof(CompactionSummarizer),
			nameof(CompactionSummarizer.AddMemoryAsync),
			nameof(CompactionSummarizer.RemoveMemoryAsync),
			nameof(CompactionSummarizer.SummarizeHistoryAsync));

		Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
		{
			[LlmRole.Planning] = planningTools,
			[LlmRole.Developer] = developerTools,
			[LlmRole.Compaction] = compactionTools,
			[LlmRole.SubAgent] = subAgentTools
		};

		return result;
	}
}
