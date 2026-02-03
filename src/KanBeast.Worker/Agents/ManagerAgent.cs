using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Agents;

// Defines manager responsibilities for task planning and verification.
public interface IManagerAgent
{
    Task<List<string>> BreakDownTicketAsync(TicketDto ticket);
    Task<bool> VerifyTaskCompletionAsync(string taskDescription, string workDir);
    Task<bool> AllTasksCompleteAsync(TicketDto ticket);
}

// Coordinates manager workflows for task breakdown and verification.
public class ManagerAgent : IManagerAgent
{
    private readonly IKanbanApiClient _apiClient;
    private readonly IToolExecutor _toolExecutor;
    private readonly string _systemPrompt;
    private readonly ILlmService _llmService;
    private readonly Kernel _kernel;

    public ManagerAgent(
        IKanbanApiClient apiClient,
        IToolExecutor toolExecutor,
        string systemPrompt,
        ILlmService llmService,
        Kernel kernel)
    {
        _apiClient = apiClient;
        _toolExecutor = toolExecutor;
        _systemPrompt = systemPrompt;
        _llmService = llmService;
        _kernel = kernel;
    }

    public async Task<List<string>> BreakDownTicketAsync(TicketDto ticket)
    {
        await _apiClient.AddActivityLogAsync(ticket.Id, "Manager: Analyzing ticket and breaking down into tasks");

        List<string> contextStatements = new List<string>();
        contextStatements.Add($"Ticket Title: {ticket.Title}");
        contextStatements.Add($"Ticket Description: {ticket.Description}");
        await UpdateContextStatementsAsync(contextStatements);

        string userPrompt = "Break this ticket into ordered, testable subtasks with acceptance criteria. Return one task per line.";
        string response = await _llmService.RunAsync(_kernel, _systemPrompt, userPrompt, CancellationToken.None);
        List<string> tasks = ParseTasks(response);

        if (tasks.Count == 0)
        {
            tasks.Add($"Implement core functionality for: {ticket.Title}");
        }

        List<KanbanSubtaskDto> subtasks = new List<KanbanSubtaskDto>();
        foreach (string task in tasks)
        {
            KanbanSubtaskDto subtask = new KanbanSubtaskDto
            {
                Id = Guid.NewGuid().ToString(),
                Name = task,
                Description = string.Empty,
                Status = SubtaskStatus.Incomplete
            };

            subtasks.Add(subtask);
        }

        KanbanTaskDto kanbanTask = new KanbanTaskDto
        {
            Id = Guid.NewGuid().ToString(),
            Name = ticket.Title,
            Description = ticket.Description,
            Subtasks = subtasks
        };

        await _apiClient.AddTaskAsync(ticket.Id, kanbanTask);
        await _apiClient.AddActivityLogAsync(ticket.Id, $"Manager: Added task with {kanbanTask.Subtasks.Count} subtasks");

        return tasks;
    }

    public async Task<bool> VerifyTaskCompletionAsync(string taskDescription, string workDir)
    {
        List<string> contextStatements = new List<string>();
        contextStatements.Add($"Task: {taskDescription}");
        contextStatements.Add($"Working directory: {workDir}");
        await UpdateContextStatementsAsync(contextStatements);

        string userPrompt = "Verify the current task is complete and correct. Use available tools if needed. Respond with 'APPROVED' or 'REJECTED: <reason>'.";
        string response = await _llmService.RunAsync(_kernel, _systemPrompt, userPrompt, CancellationToken.None);
        bool approved = false;

        if (response.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            approved = true;
        }

        return approved;
    }

    public Task<bool> AllTasksCompleteAsync(TicketDto ticket)
    {
        bool allComplete = true;

        foreach (KanbanTaskDto task in ticket.Tasks)
        {
            foreach (KanbanSubtaskDto subtask in task.Subtasks)
            {
                if (subtask.Status != SubtaskStatus.Complete)
                {
                    allComplete = false;
                }
            }
        }

        return Task.FromResult(allComplete);
    }

    private static List<string> ParseTasks(string response)
    {
        List<string> tasks = new List<string>();
        string[] lines = response.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            trimmed = trimmed.TrimStart('-', '*').Trim();
            trimmed = TrimNumberedPrefix(trimmed);

            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                bool alreadyAdded = false;
                foreach (string existing in tasks)
                {
                    if (string.Equals(existing, trimmed, StringComparison.Ordinal))
                    {
                        alreadyAdded = true;
                        break;
                    }
                }

                if (!alreadyAdded)
                {
                    tasks.Add(trimmed);
                }
            }
        }

        return tasks;
    }

    private static string TrimNumberedPrefix(string line)
    {
        string trimmed = line.Trim();
        int dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0 && int.TryParse(trimmed[..dotIndex], out int _))
        {
            return trimmed[(dotIndex + 1)..].Trim();
        }

        return trimmed;
    }

    private async Task UpdateContextStatementsAsync(List<string> statements)
    {
        await _llmService.ClearContextStatementsAsync(CancellationToken.None);

        foreach (string statement in statements)
        {
            await _llmService.AddContextStatementAsync(statement, CancellationToken.None);
        }
    }
}
