using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Agents;

public interface IDeveloperAgent
{
    Task<bool> WorkOnTaskAsync(string taskDescription, string workDir);
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

    public async Task<bool> WorkOnTaskAsync(string taskDescription, string workDir)
    {
        await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: Starting work on - {taskDescription}");

        try
        {
            var userPrompt = $"Task: {taskDescription}\nWorking directory: {workDir}\n\nImplement the task using available tools. When done, summarize changes and test results.";
            var response = await _llmService.RunAsync(_kernel, _systemPrompt, userPrompt);
            await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: {response}");
            return true;
        }
        catch (Exception ex)
        {
            await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: Error - {ex.Message}");
            return false;
        }
    }
}
