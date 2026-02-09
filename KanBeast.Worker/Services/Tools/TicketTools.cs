using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to interact with the ticket system (planning phase only).
public class TicketTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly IKanbanApiClient _apiClient;
    private readonly TicketHolder _ticketHolder;
    private readonly Dictionary<LlmRole, List<Tool>> _toolsByRole;

    public TicketTools(IKanbanApiClient apiClient, TicketHolder ticketHolder)
    {
        _apiClient = apiClient;
        _ticketHolder = ticketHolder;
        _toolsByRole = BuildToolsByRole();
    }

    private Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
    {
        List<Tool> planningTools = new List<Tool>();
        ToolHelper.AddTools(planningTools, this,
            nameof(LogMessageAsync),
            nameof(CreateTaskAsync),
            nameof(CreateSubtaskAsync));

        List<Tool> logOnlyTools = new List<Tool>();
        ToolHelper.AddTools(logOnlyTools, this,
            nameof(LogMessageAsync));

        Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
        {
            [LlmRole.Planning] = planningTools,
            [LlmRole.QA] = logOnlyTools,
            [LlmRole.Developer] = logOnlyTools,
            [LlmRole.Compaction] = new List<Tool>()
        };

        return result;
    }

    public void AddTools(List<Tool> tools, LlmRole role)
    {
        if (_toolsByRole.TryGetValue(role, out List<Tool>? roleTools))
        {
            tools.AddRange(roleTools);
        }
    }

    [Description("Send a message to the human's display about discoveries, decisions, important details, occasional jokes, etc.")]
    public async Task<ToolResult> LogMessageAsync(
        [Description("Message to log")] string message,
        CancellationToken cancellationToken)
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
                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);
                await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, message);
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

    private string? FindTaskIdByName(string taskName)
    {
        string? result = null;

        foreach (KanbanTaskDto task in _ticketHolder.Ticket.Tasks)
        {
            if (string.Equals(task.Name, taskName, StringComparison.Ordinal))
            {
                result = task.Id;
                break;
            }
        }

        return result;
    }

    [Description("Create a new task for the ticket.")]
    public async Task<ToolResult> CreateTaskAsync(
        [Description("Name of the task")] string taskName,
        [Description("Description of the task")] string taskDescription,
        CancellationToken cancellationToken)
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

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);
                TicketDto? updated = await _apiClient.AddTaskToTicketAsync(_ticketHolder.Ticket.Id, task);

                if (updated == null)
                {
                    result = new ToolResult("Error: API returned null when creating task");
                }
                else
                {
                    _ticketHolder.Update(updated);
                    await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created task '{taskName}'");
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
    public async Task<ToolResult> CreateSubtaskAsync(
        [Description("Name of the task to add the subtask to")] string taskName,
        [Description("Short name for the subtask")] string subtaskName,
        [Description("Detailed description including acceptance criteria")] string subtaskDescription,
        CancellationToken cancellationToken)
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
            string? taskId = FindTaskIdByName(taskName);

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

                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(DefaultTimeout);
                    TicketDto? updated = await _apiClient.AddSubtaskToTaskAsync(_ticketHolder.Ticket.Id, taskId, subtask);

                    if (updated == null)
                    {
                        result = new ToolResult("Error: API returned null when creating subtask");
                    }
                    else
                    {
                        _ticketHolder.Update(updated);
                        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created subtask '{subtaskName}' under task '{taskName}'");
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
