using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Agents;

public interface IManagerAgent
{
    Task<List<string>> BreakDownTicketAsync(TicketDto ticket);
    Task<bool> VerifyTaskCompletionAsync(string taskDescription, string workDir);
    Task<bool> AllTasksCompleteAsync(TicketDto ticket);
}

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

        var userPrompt = $"Ticket Title: {ticket.Title}\nTicket Description: {ticket.Description}\n\nBreak this ticket into ordered, testable subtasks with acceptance criteria. Return one task per line.";
        var response = await _llmService.RunAsync(_kernel, _systemPrompt, userPrompt);
        var tasks = ParseTasks(response);

        if (tasks.Count == 0)
        {
            tasks.Add($"Implement core functionality for: {ticket.Title}");
        }

        // Add tasks to the ticket
        foreach (var task in tasks)
        {
            await _apiClient.AddTaskAsync(ticket.Id, task);
            await _apiClient.AddActivityLogAsync(ticket.Id, $"Manager: Added task - {task}");
        }

        return tasks;
    }

    public async Task<bool> VerifyTaskCompletionAsync(string taskDescription, string workDir)
    {
        var userPrompt = $"Verify the following task is complete and correct. Use available tools if needed.\nTask: {taskDescription}\nWorking directory: {workDir}\n\nRespond with 'APPROVED' or 'REJECTED: <reason>'.";
        var response = await _llmService.RunAsync(_kernel, _systemPrompt, userPrompt);

        if (response.Contains("APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public async Task<bool> AllTasksCompleteAsync(TicketDto ticket)
    {
        return ticket.Tasks.All(t => t.IsCompleted);
    }

    private static List<string> ParseTasks(string response)
    {
        return response
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('-', '*').Trim())
            .Select(line => TrimNumberedPrefix(line))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct()
            .ToList();
    }

    private static string TrimNumberedPrefix(string line)
    {
        var trimmed = line.Trim();
        var dotIndex = trimmed.IndexOf('.');
        if (dotIndex > 0 && int.TryParse(trimmed[..dotIndex], out _))
        {
            return trimmed[(dotIndex + 1)..].Trim();
        }

        return trimmed;
    }
}
