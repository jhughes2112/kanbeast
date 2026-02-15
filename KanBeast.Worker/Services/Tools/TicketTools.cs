using System.ComponentModel;
using System.Text;
using KanBeast.Shared;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to interact with the ticket system.
public static class TicketTools
{
    [Description("Signal that planning is complete and implementation should begin. Call this when all tasks and subtasks have been created.")]
    public static Task<ToolResult> PlanningCompleteAsync(ToolContext context)
    {
        ToolResult result = new ToolResult("Planning complete. Beginning implementation phase.", true);
        return Task.FromResult(result);
    }

    [Description("Signal that you have finished working on the current subtask. Call this when your work is complete or is blocked in some way.")]
    public static Task<ToolResult> EndSubtaskAsync(
        [Description("Summary of what you accomplished or a detailed explanation of what the blockers are")] string summary,
        ToolContext context)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(summary))
        {
            result = new ToolResult("Error: Summary cannot be empty", false);
        }
        else
        {
            result = new ToolResult(summary, true);
        }

        return Task.FromResult(result);
    }

    [Description("Delete all tasks and subtasks to start planning over. Use this if the current plan is fundamentally wrong.")]
    public static async Task<ToolResult> DeleteAllTasksAsync(ToolContext context)
    {
        ToolResult result;

        Ticket? updated = await WorkerSession.ApiClient.DeleteAllTasksAsync(WorkerSession.TicketHolder.Ticket.Id, WorkerSession.CancellationToken);
        if (updated != null)
        {
            WorkerSession.TicketHolder.Update(updated);
            await WorkerSession.ApiClient.AddActivityLogAsync(WorkerSession.TicketHolder.Ticket.Id, "Manager: Deleted all tasks to restart planning", WorkerSession.CancellationToken);
            result = new ToolResult("All tasks and subtasks deleted. You can now create a new plan.", false);
        }
        else
        {
            result = new ToolResult("Error: Failed to delete tasks", false);
        }

        return result;
    }

    [Description("Send a message to the human's display about discoveries, decisions, important details, occasional jokes, etc.")]
    public static async Task<ToolResult> LogMessageAsync(
        [Description("Message to log")] string message,
        ToolContext context)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(message))
        {
            result = new ToolResult("Error: Message cannot be empty", false);
        }
        else
        {
            try
            {
                await WorkerSession.ApiClient.AddActivityLogAsync(WorkerSession.TicketHolder.Ticket.Id, message, WorkerSession.CancellationToken);
                result = new ToolResult("Message logged", false);
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult("Error: Request timed out or cancelled while logging message", false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to log message: {ex.Message}", false);
            }
        }

        return result;
    }

    [Description("""
		Create a new task to organize the work required for this ticket. Tasks serve two primary purposes: (1) Provide high-level visibility to the user of major work areas, and (2) Break down complex ticket requirements into manageable units of work.
		Tasks represent logical groupings of related work. Choose task granularity that makes sense for the ticket:
		- For simple tickets: A single task may be sufficient if the work is cohesive and straightforward
		- For complex tickets: Create multiple tasks to separate concerns (e.g., 'Database Schema Changes', 'API Implementation', 'UI Updates', 'Testing & Documentation')
		
		Tasks do NOT require subtasks. Simple, atomic work should be a task without subtasks. Only add subtasks when a task genuinely needs decomposition into multiple sequential or parallel steps.
		
		The developer will work through tasks in order. For tasks with subtasks, all subtasks are completed before the task itself is visited, allowing the task to serve as a final integration/verification step if needed.
		
		Guidelines:
		- Use clear, descriptive task names that communicate the work area to the user
		- Task descriptions should outline the overall goal and scope
		- Consider user visibility: tasks appear in the activity log and provide progress insight
		- Balance granularity: too few tasks hide progress, too many tasks create noise
		- Tasks are your primary organizational tool for decomposing the ticket requirements
		""")]
    public static async Task<ToolResult> CreateTaskAsync(
        [Description("Name of the task")] string taskName,
        [Description("Description of the task")] string taskDescription,
        ToolContext context)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = new ToolResult("Error: Task name cannot be empty", false);
        }
        else
        {
            try
            {
                KanbanTask task = new KanbanTask
                {
                    Name = taskName,
                    Description = taskDescription,
                    Subtasks = new List<KanbanSubtask>()
                };

                Ticket? updated = await WorkerSession.ApiClient.AddTaskToTicketAsync(WorkerSession.TicketHolder.Ticket.Id, task, WorkerSession.CancellationToken);

                if (updated == null)
                {
                    result = new ToolResult("Error: API returned null when creating task", false);
                }
                else
                {
                    WorkerSession.TicketHolder.Update(updated);
                    await WorkerSession.ApiClient.AddActivityLogAsync(WorkerSession.TicketHolder.Ticket.Id, $"Created task '{taskName}'", WorkerSession.CancellationToken);
                    result = new ToolResult(FormatTicketSummary(updated, $"SUCCESS: Created task '{taskName}'"), false);
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult("Error: Request timed out or cancelled while creating task", false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to create task: {ex.Message}", false);
            }
        }

        return result;
    }

    [Description("""
		Create a subtask to break down a task into smaller, actionable work units. Subtasks are the actual units of work that the developer agent will complete.

		Use subtasks to decompose a task when:
		- The task involves multiple distinct technical steps that should be completed sequentially
		- Different parts of the task require different skills or approaches (e.g., database work, then API, then UI)
		- You want to parallelize work or create logical checkpoints
		- The task is complex enough that breaking it down aids planning and tracking

		Do NOT create subtasks if:
		- The task is simple and atomic (can be completed in one focused work session)
		- Adding subtasks would just create unnecessary overhead
		- The work is cohesive enough to be done as a single unit

		Subtask descriptions MUST include:
		- Clear, specific acceptance criteria that define 'done'
		- Technical approach or key implementation details
		- Any constraints, dependencies, or gotchas
		- Expected outcomes or deliverables

		The developer works through a task's subtasks in order before visiting the task itself. This allows the task to optionally serve as a final integration or verification step.

		Guidelines:
		- Subtask names should be action-oriented and specific (e.g., 'Add userId column to posts table' not 'Database changes')
		- Each subtask should represent 15 minutes of focused work or less
		- Write descriptions as if briefing another engineer on what work to perform (but do not perform the work, trust the developer to do that)
		- Include enough detail that the developer can work autonomously
		- Consider the developer's context: they can read files, search code, run commands, but need clear direction on the goal
		""")]
    public static async Task<ToolResult> CreateSubtaskAsync(
        [Description("Name of the task to add the subtask to")] string taskName,
        [Description("Short name for the subtask")] string subtaskName,
        [Description("Detailed description including acceptance criteria")] string subtaskDescription,
        ToolContext context)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = new ToolResult("Error: Task name cannot be empty", false);
        }
        else if (string.IsNullOrWhiteSpace(subtaskName))
        {
            result = new ToolResult("Error: Subtask name cannot be empty", false);
        }
        else
        {
            string? taskId = WorkerSession.TicketHolder.FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = new ToolResult($"Error: Task '{taskName}' not found", false);
            }
            else
            {
                try
                {
                    KanbanSubtask subtask = new KanbanSubtask
                    {
                        Name = subtaskName,
                        Description = subtaskDescription,
                        Status = SubtaskStatus.Incomplete
                    };

                    Ticket? updated = await WorkerSession.ApiClient.AddSubtaskToTaskAsync(WorkerSession.TicketHolder.Ticket.Id, taskId, subtask, WorkerSession.CancellationToken);

                    if (updated == null)
                    {
                        result = new ToolResult("Error: API returned null when creating subtask", false);
                    }
                    else
                    {
                        WorkerSession.TicketHolder.Update(updated);
                        await WorkerSession.ApiClient.AddActivityLogAsync(WorkerSession.TicketHolder.Ticket.Id, $"Created subtask '{subtaskName}' under task '{taskName}'", WorkerSession.CancellationToken);
                        result = new ToolResult(FormatTicketSummary(updated, $"SUCCESS: Created subtask '{subtaskName}' under task '{taskName}'"), false);
                    }
                }
                catch (OperationCanceledException)
                {
                    result = new ToolResult("Error: Request timed out or cancelled while creating subtask", false);
                }
                catch (Exception ex)
                {
                    result = new ToolResult($"Error: Failed to create subtask: {ex.Message}", false);
                }
            }
        }

        return result;
    }

    [Description("""
        Get the next subtask (or task without subtasks) that needs work, along with the list of available LLMs you can delegate to.
        Call this to find what to work on next.
        The response includes:
        - The next work item details (task name, subtask name, IDs, description)
        - A list of available LLM configurations with their strengths, weaknesses, and pricing
        - Paid models are automatically filtered out when the ticket has no remaining budget
        If there is no remaining work, the response will indicate all work is complete.
        If no LLMs are available, the response will indicate the work is blocked.
        """)]
    public static Task<ToolResult> GetNextWorkItemAsync(ToolContext context)
    {
        Ticket ticket = WorkerSession.TicketHolder.Ticket;

        // Find the next incomplete subtask, or first task with no subtasks that hasn't been completed.
        string? nextTaskId = null;
        string? nextTaskName = null;
        string? nextTaskDescription = null;
        string? nextSubtaskId = null;
        string? nextSubtaskName = null;
        string? nextSubtaskDescription = null;

        foreach (KanbanTask task in ticket.Tasks)
        {
            if (task.Subtasks.Count == 0)
            {
                // Task with no subtasks — treat the task itself as a work item.
                nextTaskId = task.Id;
                nextTaskName = task.Name;
                nextTaskDescription = task.Description;
                break;
            }

            foreach (KanbanSubtask subtask in task.Subtasks)
            {
                if (subtask.Status != SubtaskStatus.Complete)
                {
                    nextTaskId = task.Id;
                    nextTaskName = task.Name;
                    nextTaskDescription = task.Description;
                    nextSubtaskId = subtask.Id;
                    nextSubtaskName = subtask.Name;
                    nextSubtaskDescription = subtask.Description;
                    break;
                }
            }

            if (nextTaskId != null)
            {
                break;
            }
        }

        StringBuilder sb = new StringBuilder();

        if (nextTaskId == null)
        {
            sb.AppendLine("ALL WORK COMPLETE: No remaining incomplete tasks or subtasks.");
            sb.AppendLine("You should move the ticket to Done status if all work is verified.");
            ToolResult doneResult = new ToolResult(sb.ToString(), false);
            return Task.FromResult(doneResult);
        }

        sb.AppendLine("NEXT WORK ITEM:");
        sb.AppendLine($"  Task: {nextTaskName} (id: {nextTaskId})");
        sb.AppendLine($"  Task Description: {nextTaskDescription}");

        if (nextSubtaskId != null)
        {
            sb.AppendLine($"  Subtask: {nextSubtaskName} (id: {nextSubtaskId})");
            sb.AppendLine($"  Subtask Description: {nextSubtaskDescription}");
        }
        else
        {
            sb.AppendLine("  (This task has no subtasks — delegate it directly as a single work item)");
        }

        sb.AppendLine();

        // Build available LLM list, filtering paid models when budget is exhausted.
        bool hasBudget = ticket.MaxCost <= 0 || ticket.LlmCost < ticket.MaxCost;
        bool includePaid = hasBudget;
        List<(string id, string model, string strengths, string weaknesses, bool isPaid, bool isAvailable)> llms =
            WorkerSession.LlmProxy.GetAvailableLlmSummaries(includePaid);

        // Filter to only available LLMs.
        List<(string id, string model, string strengths, string weaknesses, bool isPaid, bool isAvailable)> availableLlms = new();
        foreach ((string id, string model, string strengths, string weaknesses, bool isPaid, bool isAvailable) llm in llms)
        {
            if (llm.isAvailable)
            {
                availableLlms.Add(llm);
            }
        }

        if (availableLlms.Count == 0)
        {
            sb.AppendLine("BLOCKED: No LLMs are available to perform this work.");

            if (!includePaid)
            {
                sb.AppendLine("  Reason: Budget exhausted and all remaining LLMs are paid.");
            }
            else
            {
                sb.AppendLine("  Reason: All configured LLMs are permanently down or unavailable.");
            }

            ToolResult blockedResult = new ToolResult(sb.ToString(), false);
            return Task.FromResult(blockedResult);
        }

        sb.AppendLine("AVAILABLE LLMs (choose one to delegate this work to via start_developer):");

        foreach ((string id, string model, string strengths, string weaknesses, bool isPaid, bool isAvailable) llm in availableLlms)
        {
            sb.AppendLine($"  - id: {llm.id}");
            sb.AppendLine($"    model: {llm.model}");
            sb.AppendLine($"    paid: {(llm.isPaid ? "yes" : "no")}");

            if (!string.IsNullOrWhiteSpace(llm.strengths))
            {
                sb.AppendLine($"    strengths: {llm.strengths}");
            }

            if (!string.IsNullOrWhiteSpace(llm.weaknesses))
            {
                sb.AppendLine($"    weaknesses: {llm.weaknesses}");
            }
        }

        ToolResult result = new ToolResult(sb.ToString(), false);
        return Task.FromResult(result);
    }

    private static string FormatTicketSummary(Ticket ticket, string header)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine($"Ticket: {ticket.Title} (Status: {ticket.Status})");
        sb.AppendLine("Tasks:");

        foreach (KanbanTask task in ticket.Tasks)
        {
            sb.AppendLine($"  - {task.Name}");
            foreach (KanbanSubtask subtask in task.Subtasks)
            {
                sb.AppendLine($"      [{subtask.Status}] {subtask.Name}");
            }
        }

        return sb.ToString();
    }
}
