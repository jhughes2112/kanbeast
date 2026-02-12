using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;

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

        TicketDto? updated = await WorkerSession.ApiClient.DeleteAllTasksAsync(WorkerSession.TicketHolder.Ticket.Id, WorkerSession.CancellationToken);
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

    [Description("Create a new task for the ticket.")]
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

                TicketDto? updated = await WorkerSession.ApiClient.AddTaskToTicketAsync(WorkerSession.TicketHolder.Ticket.Id, task, WorkerSession.CancellationToken);

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

    [Description("Create a subtask for an existing task. Include clear acceptance criteria in the description.")]
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
            string? taskId = WorkerSession.TicketHolder.Ticket.FindTaskIdByName(taskName);

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

                    TicketDto? updated = await WorkerSession.ApiClient.AddSubtaskToTaskAsync(WorkerSession.TicketHolder.Ticket.Id, taskId, subtask, WorkerSession.CancellationToken);

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

    private static string FormatTicketSummary(TicketDto ticket, string header)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine(header);
        sb.AppendLine();
        sb.AppendLine($"Ticket: {ticket.Title} (Status: {ticket.Status})");
        sb.AppendLine("Tasks:");

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            sb.AppendLine($"  - {task.Name}");
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                sb.AppendLine($"      [{subtask.Status}] {subtask.Name}");
            }
        }

        return sb.ToString();
    }
}
