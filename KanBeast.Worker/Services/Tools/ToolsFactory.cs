using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// Centralized static registry of all tools, keyed by role.
// Planning tools are composed at runtime based on ticket state.
public static class ToolsFactory
{
	private static readonly Dictionary<LlmRole, List<Tool>> ToolsByRole = BuildToolsByRole();
	private static readonly List<Tool> ActiveOnlyPlanningTools = BuildActiveOnlyPlanningTools();

	public static List<Tool> GetTools(LlmRole role)
	{
		if (!ToolsByRole.TryGetValue(role, out List<Tool>? tools))
		{
			return new List<Tool>();
		}

		if (role == LlmRole.Planning && WorkerSession.TicketHolder.Ticket.Status == TicketStatus.Active)
		{
			List<Tool> composed = new List<Tool>(tools);
			composed.AddRange(ActiveOnlyPlanningTools);
			return composed;
		}

		return tools;
	}

	private static List<Tool> BuildActiveOnlyPlanningTools()
	{
		List<Tool> tools = new List<Tool>();
		ToolHelper.AddTools(tools, typeof(DeveloperTools), nameof(DeveloperTools.StartDeveloperAsync));
		ToolHelper.AddTools(tools, typeof(TicketTools), nameof(TicketTools.GetNextWorkItemAsync));
		return tools;
	}

	private static Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
	{
		List<Tool> planningTools = new List<Tool>();
		ToolHelper.AddTools(planningTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(planningTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync));
		ToolHelper.AddTools(planningTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(planningTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(planningTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync), nameof(TicketTools.PlanningCompleteAsync), nameof(TicketTools.DeleteAllTasksAsync));

		List<Tool> developerTools = new List<Tool>();
		ToolHelper.AddTools(developerTools, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(developerTools, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(developerTools, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(developerTools, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(developerTools, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(developerTools, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developerTools, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));

		// Sub-agent tools: same as developer minus sub-agent spawning.
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
			[LlmRole.Developer] = developerTools,
			[LlmRole.Compaction] = compactionTools,
			[LlmRole.SubAgent] = subAgentTools
		};

		return result;
	}
}
