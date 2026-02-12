using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to interact with the ticket system (planning phase only).
public static class TicketTools
{
    [Description("Send a message to the human's display about discoveries, decisions, important details, occasional jokes, etc.")]
    public static async Task<ToolResult> LogMessageAsync(
        [Description("Message to log")] string message,
        ToolContext context)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(message))
        {
            result = new ToolResult("Error: Message cannot be empty");
        }
        else
        {
            try
            {
                await context.ApiClient!.AddActivityLogAsync(context.TicketHolder!.Ticket.Id, message, context.CancellationToken);
                result = new ToolResult("Message logged");
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult("Error: Request timed out or cancelled while logging message");
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to log message: {ex.Message}");
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
            result = new ToolResult("Error: Task name cannot be empty");
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

                TicketDto? updated = await context.ApiClient!.AddTaskToTicketAsync(context.TicketHolder!.Ticket.Id, task, context.CancellationToken);

                if (updated == null)
                {
                    result = new ToolResult("Error: API returned null when creating task");
                }
                else
                {
                    context.TicketHolder.Update(updated);
                    await context.ApiClient.AddActivityLogAsync(context.TicketHolder.Ticket.Id, $"Created task '{taskName}'", context.CancellationToken);
                    result = new ToolResult(FormatTicketSummary(updated, $"SUCCESS: Created task '{taskName}'"));
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult("Error: Request timed out or cancelled while creating task");
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to create task: {ex.Message}");
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
            result = new ToolResult("Error: Task name cannot be empty");
        }
        else if (string.IsNullOrWhiteSpace(subtaskName))
        {
            result = new ToolResult("Error: Subtask name cannot be empty");
        }
        else
        {
            string? taskId = context.TicketHolder!.Ticket.FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = new ToolResult($"Error: Task '{taskName}' not found");
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

                    TicketDto? updated = await context.ApiClient!.AddSubtaskToTaskAsync(context.TicketHolder!.Ticket.Id, taskId, subtask, context.CancellationToken);

                    if (updated == null)
                    {
                        result = new ToolResult("Error: API returned null when creating subtask");
                    }
                    else
                    {
                        context.TicketHolder.Update(updated);
                        await context.ApiClient.AddActivityLogAsync(context.TicketHolder.Ticket.Id, $"Created subtask '{subtaskName}' under task '{taskName}'", context.CancellationToken);
                        result = new ToolResult(FormatTicketSummary(updated, $"SUCCESS: Created subtask '{subtaskName}' under task '{taskName}'"));
                    }
                }
                catch (OperationCanceledException)
                {
                    result = new ToolResult("Error: Request timed out or cancelled while creating subtask");
                }
                catch (Exception ex)
                {
                    result = new ToolResult($"Error: Failed to create subtask: {ex.Message}");
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
