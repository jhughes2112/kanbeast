using KanBeast.Worker.Models;
using KanBeast.Worker.Services;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

public class DeveloperTaskTools
{
    private readonly IKanbanApiClient _apiClient;

    public DeveloperTaskTools(IKanbanApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [KernelFunction("complete_subtask")]
    public Task CompleteSubtaskAsync(string ticketId, string taskId, string subtaskId)
    {
        return _apiClient.UpdateSubtaskStatusAsync(ticketId, taskId, subtaskId, SubtaskStatus.Complete);
    }
}
