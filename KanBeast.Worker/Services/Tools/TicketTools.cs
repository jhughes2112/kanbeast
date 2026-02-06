using System.ComponentModel;
using System.Text;
using KanBeast.Worker.Models;
using Microsoft.Extensions.Logging;

namespace KanBeast.Worker.Services.Tools;

// Configuration for the developer conversation.
public class DeveloperConfig
{
    public required LlmProxy LlmProxy { get; init; }
    public required string Prompt { get; init; }
    public required string WorkDir { get; init; }
    public required Func<List<IToolProvider>> ToolProvidersFactory { get; init; }
    public LlmConversation? Conversation { get; set; }
}

// Tools for LLM to interact with the ticket system and communicate between manager/developer.
public class TicketTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly ILogger? _logger;
    private readonly IKanbanApiClient _apiClient;
    private readonly TicketHolder _ticketHolder;
    private readonly TaskState _state;
    private readonly DeveloperConfig? _developerConfig;
    private readonly Dictionary<LlmRole, List<Tool>> _toolsByRole;

    public TicketTools(IKanbanApiClient apiClient, TicketHolder ticketHolder, TaskState state)
        : this(null, apiClient, ticketHolder, state, null)
    {
    }

    public TicketTools(
        ILogger? logger,
        IKanbanApiClient apiClient,
        TicketHolder ticketHolder,
        TaskState state,
        DeveloperConfig? developerConfig)
    {
        _logger = logger;
        _apiClient = apiClient;
        _ticketHolder = ticketHolder;
        _state = state;
        _developerConfig = developerConfig;
        _toolsByRole = BuildToolsByRole();
    }

    private Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
    {
        List<Tool> managerTools = new List<Tool>();
        ToolHelper.AddTools(managerTools, this,
            nameof(LogMessageAsync),
            nameof(CreateTaskAsync),
            nameof(CreateSubtaskAsync),
            nameof(AssignSubtaskToDeveloperAsync),
            nameof(TellDeveloperAsync),
            nameof(SetSubtaskStatusAsync),
            nameof(SetTicketStatusAsync));

        List<Tool> developerTools = new List<Tool>();
        ToolHelper.AddTools(developerTools, this,
            nameof(LogMessageAsync),
            nameof(TellManagerAsync));

        Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
        {
            [LlmRole.Manager] = managerTools,
            [LlmRole.Developer] = developerTools,
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
        else
        {
            throw new ArgumentException($"Unhandled role: {role}");
        }
    }

    [Description("Send a message to the human's display about discoveries, decisions, important details, occasional jokes, etc.")]
    public async Task<string> LogMessageAsync(
        [Description("Message to log")] string message)
    {
        string result = "Message logged";

        if (string.IsNullOrWhiteSpace(message))
        {
            result = "Error: Message cannot be empty";
        }
        else
        {
            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, message);
            }
            catch (TaskCanceledException)
            {
                result = "Error: Request timed out while logging message";
            }
            catch (Exception ex)
            {
                result = $"Error: Failed to log message: {ex.Message}";
            }
        }

        return result;
    }

    private string? FindTaskIdByName(string taskName)
    {
        foreach (KanbanTaskDto task in _ticketHolder.Ticket.Tasks)
        {
            if (string.Equals(task.Name, taskName, StringComparison.Ordinal))
            {
                return task.Id;
            }
        }

        return null;
    }

    [Description("Create a new task for the ticket.")]
    public async Task<string> CreateTaskAsync(
        [Description("Name of the task")] string taskName,
        [Description("Description of the task")] string taskDescription)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = "Error: Task name cannot be empty";
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

                using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                TicketDto? updated = await _apiClient.AddTaskToTicketAsync(_ticketHolder.Ticket.Id, task);

                if (updated == null)
                {
                    result = "Error: API returned null when creating task";
                }
                else
                {
                    _ticketHolder.Update(updated);
                    await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created task '{taskName}'");
                    result = FormatTicketSummary(updated, $"SUCCESS: Created task '{taskName}'");
                }
            }
            catch (TaskCanceledException)
            {
                result = "Error: Request timed out while creating task";
            }
            catch (Exception ex)
            {
                result = $"Error: Failed to create task: {ex.Message}";
            }
        }

        return result;
    }

    [Description("Create a subtask for an existing task. Include clear acceptance criteria in the description.")]
    public async Task<string> CreateSubtaskAsync(
        [Description("Name of the task to add the subtask to")] string taskName,
        [Description("Short name for the subtask")] string subtaskName,
        [Description("Detailed description including acceptance criteria")] string subtaskDescription)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName))
        {
            result = "Error: Task name cannot be empty";
        }
        else if (string.IsNullOrWhiteSpace(subtaskName))
        {
            result = "Error: Subtask name cannot be empty";
        }
        else
        {
            string? taskId = FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = $"Error: Task '{taskName}' not found";
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

                    using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                    TicketDto? updated = await _apiClient.AddSubtaskToTaskAsync(_ticketHolder.Ticket.Id, taskId, subtask);

                    if (updated == null)
                    {
                        result = "Error: API returned null when creating subtask";
                    }
                    else
                    {
                        _ticketHolder.Update(updated);
                        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Created subtask '{subtaskName}' under task '{taskName}'");

                        _state.SubtasksCreated = true;
                        _state.SubtaskCount += 1;

                        result = FormatTicketSummary(updated, $"SUCCESS: Created subtask '{subtaskName}' under task '{taskName}'");
                    }
                }
                catch (TaskCanceledException)
                {
                    result = "Error: Request timed out while creating subtask";
                }
                catch (Exception ex)
                {
                    result = $"Error: Failed to create subtask: {ex.Message}";
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

    [Description("Assign a subtask to the developer and start work. Sets subtask to InProgress and sends initial instructions.")]
    public async Task<string> AssignSubtaskToDeveloperAsync(
        [Description("Name of the task containing the subtask")] string taskName,
        [Description("Name of the subtask to assign")] string subtaskName,
        [Description("Instructions for the developer, skills to review, acceptance criteria")] string instructions)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName) || string.IsNullOrWhiteSpace(subtaskName))
        {
            result = "Error: Task name and subtask name are required";
        }
        else if (string.IsNullOrWhiteSpace(instructions))
        {
            result = "Error: Instructions cannot be empty";
        }
        else if (_developerConfig == null)
        {
            result = "Error: Developer configuration not available";
        }
        else
        {
            string? taskId = FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = $"Error: Task '{taskName}' not found";
            }
            else
            {
                string? subtaskId = FindSubtaskIdByName(taskId, subtaskName);

                if (subtaskId == null)
                {
                    result = $"Error: Subtask '{subtaskName}' not found in task '{taskName}'";
                }
                else
                {
                    try
                    {
                        TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, taskId, subtaskId, SubtaskStatus.InProgress);
                        if (updated != null)
                        {
                            _ticketHolder.Update(updated);
                        }

                        _state.CurrentTaskId = taskId;
                        _state.CurrentSubtaskId = subtaskId;

                        await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Assigned subtask '{subtaskName}' to developer");
                        _logger?.LogInformation("Assigned subtask '{SubtaskName}' to developer", subtaskName);

                        result = await TellDeveloperAsync($"You are now working on subtask '{subtaskName}' under task '{taskName}'.\n\n{instructions}");
                    }
                    catch (TaskCanceledException)
                    {
                        result = "Error: Request timed out while assigning subtask";
                    }
                    catch (Exception ex)
                    {
                        result = $"Error: Failed to assign subtask: {ex.Message}";
                    }
                }
            }
        }

        return result;
    }

    [Description("Send a follow-up message to the developer. Use this for additional instructions, feedback, or questions after initial assignment.")]
    public async Task<string> TellDeveloperAsync(
        [Description("Message to tell the developer")] string message)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(message))
        {
            result = "Error: Message cannot be empty";
        }
        else if (_developerConfig == null)
        {
            result = "Error: Developer configuration not available";
        }
        else
        {
            try
            {
                await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Manager → Developer: {(message.Length > 100 ? message.Substring(0, 100) + "..." : message)}");
                _logger?.LogInformation("Manager → Developer: {Message}", message.Length > 100 ? message.Substring(0, 100) + "..." : message);

                if (_developerConfig.Conversation == null)
                {
                    string initialPrompt = $"Manager says: {message}";
                    _developerConfig.Conversation = _developerConfig.LlmProxy.CreateConversation(_developerConfig.Prompt, initialPrompt);
                }
                else
                {
                    _developerConfig.Conversation.AddUserMessage($"Manager says: {message}");
                }

                _state.ClearDeveloperResponse();

                List<IToolProvider> toolProviders = _developerConfig.ToolProvidersFactory();

                while (!_state.HasDeveloperResponse)
                {
                    LlmResult llmResult = await _developerConfig.LlmProxy.ContinueAsync(_developerConfig.Conversation, toolProviders, LlmRole.Developer, CancellationToken.None);

                    if (llmResult.AccumulatedCost > 0)
                    {
                        TicketDto? updated = await _apiClient.AddLlmCostAsync(_ticketHolder.Ticket.Id, llmResult.AccumulatedCost);
                        if (updated != null)
                        {
                            _ticketHolder.Update(updated);
                        }
                    }

                    if (!llmResult.Success)
                    {
                        result = $"Developer LLM failed: {llmResult.ErrorMessage}";
                        _state.Blocked = true;
                        break;
                    }

                    if (!_state.HasDeveloperResponse)
                    {
                        _developerConfig.Conversation.AddUserMessage("Continue working. Call tell_manager when you have something to report.");
                    }
                }

                if (_state.HasDeveloperResponse)
                {
                    await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Developer → Manager: {(_state.DeveloperResponse?.Length > 100 ? _state.DeveloperResponse.Substring(0, 100) + "..." : _state.DeveloperResponse)}");
                    _logger?.LogInformation("Developer → Manager: {Message}", _state.DeveloperResponse?.Length > 100 ? _state.DeveloperResponse.Substring(0, 100) + "..." : _state.DeveloperResponse);
                    result = $"Developer response: {_state.DeveloperResponse}";
                }
            }
            catch (TaskCanceledException)
            {
                result = "Error: Request timed out while communicating with developer";
            }
            catch (Exception ex)
            {
                result = $"Error: Failed to communicate with developer: {ex.Message}";
            }
        }

        return result;
    }

    [Description("Send a response back to the manager. Call this when you have completed work, have a question, or need to report status.")]
    public Task<string> TellManagerAsync(
        [Description("Your message to the manager")] string message)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(message))
        {
            result = "Error: Message cannot be empty";
        }
        else
        {
            _state.DeveloperResponse = message;
            _state.HasDeveloperResponse = true;
            result = "Message sent to manager";
        }

        return Task.FromResult(result);
    }

    [Description("Set the status of a subtask. Use this to mark subtasks as complete, in-progress, or rejected.")]
    public async Task<string> SetSubtaskStatusAsync(
        [Description("Name of the task containing the subtask")] string taskName,
        [Description("Name of the subtask")] string subtaskName,
        [Description("New status: 'incomplete', 'in-progress', 'complete', or 'rejected'")] string status,
        [Description("Notes about the status change")] string notes)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(taskName) || string.IsNullOrWhiteSpace(subtaskName))
        {
            result = "Error: Task name and subtask name are required";
        }
        else if (string.IsNullOrWhiteSpace(status))
        {
            result = "Error: Status is required";
        }
        else
        {
            string? taskId = FindTaskIdByName(taskName);

            if (taskId == null)
            {
                result = $"Error: Task '{taskName}' not found";
            }
            else
            {
                string? subtaskId = FindSubtaskIdByName(taskId, subtaskName);

                if (subtaskId == null)
                {
                    result = $"Error: Subtask '{subtaskName}' not found in task '{taskName}'";
                }
                else
                {
                    SubtaskStatus newStatus = status.ToLowerInvariant() switch
                    {
                        "complete" or "done" => SubtaskStatus.Complete,
                        "in-progress" or "inprogress" => SubtaskStatus.InProgress,
                        "rejected" => SubtaskStatus.Rejected,
                        _ => SubtaskStatus.Incomplete
                    };

                    try
                    {
                        using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                        TicketDto? updated = await _apiClient.UpdateSubtaskStatusAsync(_ticketHolder.Ticket.Id, taskId, subtaskId, newStatus);

                        if (updated == null)
                        {
                            result = "Error: API returned null when updating subtask status";
                        }
                        else
                        {
                            _ticketHolder.Update(updated);
                            await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Subtask '{subtaskName}' status changed to {status}: {notes}");
                            result = $"Subtask '{subtaskName}' status set to {status}";
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        result = "Error: Request timed out while updating subtask status";
                    }
                    catch (Exception ex)
                    {
                        result = $"Error: Failed to update subtask status: {ex.Message}";
                    }
                }
            }
        }

        return result;
    }

    private string? FindSubtaskIdByName(string taskId, string subtaskName)
    {
        foreach (KanbanTaskDto task in _ticketHolder.Ticket.Tasks)
        {
            if (task.Id == taskId)
            {
                foreach (KanbanSubtaskDto subtask in task.Subtasks)
                {
                    if (string.Equals(subtask.Name, subtaskName, StringComparison.Ordinal))
                    {
                        return subtask.Id;
                    }
                }
            }
        }

        return null;
    }

    [Description("Set the ticket status. Use 'done' when all work is complete, 'blocked' if human intervention is required.")]
    public async Task<string> SetTicketStatusAsync(
        [Description("New status: 'done' or 'blocked'")] string status,
        [Description("Summary of what was accomplished or reason for blocking")] string notes)
    {
        string result = string.Empty;

        if (string.IsNullOrWhiteSpace(status))
        {
            result = "Error: Status is required";
        }
        else if (string.IsNullOrWhiteSpace(notes))
        {
            result = "Error: Notes are required";
        }
        else
        {
            string normalizedStatus = status.ToLowerInvariant();
            string ticketStatus = normalizedStatus == "done" || normalizedStatus == "complete" ? "Done" : "Failed";

            try
            {
                using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
                TicketDto? updated = await _apiClient.UpdateTicketStatusAsync(_ticketHolder.Ticket.Id, ticketStatus);

                if (updated == null)
                {
                    result = "Error: API returned null when updating ticket status";
                }
                else
                {
                    _ticketHolder.Update(updated);
                    await _apiClient.AddActivityLogAsync(_ticketHolder.Ticket.Id, $"Ticket {status}: {notes}");

                    if (ticketStatus == "Done")
                    {
                        _state.TicketComplete = true;
                    }
                    else
                    {
                        _state.TicketComplete = false;
                        _state.BlockedReason = notes;
                        _state.Blocked = true;
                    }

                    result = $"Ticket status set to {status}";
                }
            }
            catch (TaskCanceledException)
            {
                result = "Error: Request timed out while updating ticket status";
            }
            catch (Exception ex)
            {
                result = $"Error: Failed to update ticket status: {ex.Message}";
            }
        }

        return result;
    }
}
