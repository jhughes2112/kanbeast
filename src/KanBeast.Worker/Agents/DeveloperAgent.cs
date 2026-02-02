using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Agents;

public interface IDeveloperAgent
{
    Task<bool> WorkOnTaskAsync(KanbanTaskDto task, KanbanSubtaskDto subtask, string ticketId, string workDir);
}

public class DeveloperAgent : IDeveloperAgent
{
    private readonly IToolExecutor _toolExecutor;
    private readonly IKanbanApiClient _apiClient;
    private readonly string _systemPrompt;
    private readonly string _ticketId;
    private readonly ILlmService _llmService;
    private readonly Kernel _kernel;

    public DeveloperAgent(
        IToolExecutor toolExecutor,
        IKanbanApiClient apiClient,
        string systemPrompt,
        string ticketId,
        ILlmService llmService,
        Kernel kernel)
    {
        _toolExecutor = toolExecutor;
        _apiClient = apiClient;
        _systemPrompt = systemPrompt;
        _ticketId = ticketId;
        _llmService = llmService;
        _kernel = kernel;
    }

    public async Task<bool> WorkOnTaskAsync(KanbanTaskDto task, KanbanSubtaskDto subtask, string ticketId, string workDir)
    {
        await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: Starting work on - {subtask.Name}");
        bool success = false;

        try
        {
            string userPrompt = $"Task: {task.Name}\nTask Description: {task.Description}\nSubtask: {subtask.Name}\nSubtask Description: {subtask.Description}\nTicket Id: {ticketId}\nTask Id: {task.Id}\nSubtask Id: {subtask.Id}\nWorking directory: {workDir}\n\nImplement the subtask using available tools. When finished, call complete_subtask with the ids. Summarize changes and test results.";
            string response = await _llmService.RunAsync(_kernel, _systemPrompt, userPrompt, CancellationToken.None);
            await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: {response}");
            success = true;
        }
        catch (Exception ex)
        {
            await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: Error - {ex.Message}");
        }

        return success;
    }
}
