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
		SubAgent
	}

	private static readonly Dictionary<ToolSet, List<Tool>> Tools = BuildAllToolSets();

	public static List<Tool> GetTools(ILlmConversation conversation)
	{
		TicketStatus status = WorkerSession.TicketHolder.Ticket.Status;

		ToolSet key = conversation.Role switch
		{
			LlmRole.Planning when status == TicketStatus.Backlog => ToolSet.PlanningBacklog,
			LlmRole.Planning when status == TicketStatus.Active => ToolSet.PlanningActive,
			LlmRole.Planning => ToolSet.PlanningOther,
			LlmRole.Developer => ToolSet.Developer,
			LlmRole.SubAgent => ToolSet.SubAgent,
			_ => ToolSet.PlanningOther
		};

		List<Tool> roleTools;
		if (Tools.TryGetValue(key, out List<Tool>? cached))
		{
			roleTools = cached;
		}
		else
		{
			roleTools = new List<Tool>();
		}

		IReadOnlyList<Tool> conversationTools = conversation.GetAdditionalTools();
		if (conversationTools.Count == 0)
		{
			return roleTools;
		}

		List<Tool> merged = new List<Tool>(roleTools);
		foreach (Tool tool in conversationTools)
		{
			merged.Add(tool);
		}

		return merged;
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

		// Planning + Backlog: common + task creation tools.
		List<Tool> planningBacklog = new List<Tool>(commonPlanningTools);
		ToolHelper.AddTools(planningBacklog, typeof(TicketTools), nameof(TicketTools.CreateTaskAsync), nameof(TicketTools.CreateSubtaskAsync), nameof(TicketTools.DeleteAllTasksAsync));

		// Planning + Active: common + developer orchestration tools.
		List<Tool> planningActive = new List<Tool>(commonPlanningTools);
		ToolHelper.AddTools(planningActive, typeof(DeveloperTools), nameof(DeveloperTools.StartDeveloperAsync));
		ToolHelper.AddTools(planningActive, typeof(TicketTools), nameof(TicketTools.GetNextWorkItemAsync), nameof(TicketTools.UpdateLlmNotesAsync), nameof(TicketTools.SetTicketStatusAsync));

		// Developer: full capabilities including sub-agents.
		List<Tool> developer = new List<Tool>();
		ToolHelper.AddTools(developer, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(developer, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(developer, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(developer, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(developer, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(developer, typeof(TicketTools), nameof(TicketTools.LogMessageAsync), nameof(TicketTools.EndSubtaskAsync));
		ToolHelper.AddTools(developer, typeof(SubAgentTools), nameof(SubAgentTools.StartSubAgentAsync));

		// Sub-agent: same as developer minus sub-agent spawning.
		List<Tool> subAgent = new List<Tool>();
		ToolHelper.AddTools(subAgent, typeof(ShellTools), nameof(ShellTools.RunCommandAsync));
		ToolHelper.AddTools(subAgent, typeof(PersistentShellTools), nameof(PersistentShellTools.StartShellAsync), nameof(PersistentShellTools.SendShellAsync), nameof(PersistentShellTools.KillShellAsync));
		ToolHelper.AddTools(subAgent, typeof(FileTools), nameof(FileTools.ReadFileAsync), nameof(FileTools.GetFileAsync), nameof(FileTools.WriteFileAsync), nameof(FileTools.EditFileAsync), nameof(FileTools.MultiEditFileAsync));
		ToolHelper.AddTools(subAgent, typeof(SearchTools), nameof(SearchTools.GlobAsync), nameof(SearchTools.GrepAsync), nameof(SearchTools.ListDirectoryAsync));
		ToolHelper.AddTools(subAgent, typeof(WebTools), nameof(WebTools.GetWebPageAsync), nameof(WebTools.SearchWebAsync));
		ToolHelper.AddTools(subAgent, typeof(TicketTools), nameof(TicketTools.LogMessageAsync));
		ToolHelper.AddTools(subAgent, typeof(SubAgentTools), nameof(SubAgentTools.AgentTaskCompleteAsync));

		return new Dictionary<ToolSet, List<Tool>>
		{
			[ToolSet.PlanningBacklog] = planningBacklog,
			[ToolSet.PlanningActive] = planningActive,
			[ToolSet.PlanningOther] = commonPlanningTools,
			[ToolSet.Developer] = developer,
			[ToolSet.SubAgent] = subAgent
		};
	}
}
