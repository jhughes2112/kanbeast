using KanBeast.Worker.Services;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

public class KanbanTools
{
    private readonly IKanbanApiClient _apiClient;

    public KanbanTools(IKanbanApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    [KernelFunction("update_ticket_status")]
    public Task UpdateTicketStatusAsync(string ticketId, string status)
    {
        return _apiClient.UpdateTicketStatusAsync(ticketId, status);
    }

    [KernelFunction("add_task")]
    public Task AddTaskAsync(string ticketId, string description)
    {
        return _apiClient.AddTaskAsync(ticketId, description);
    }

    [KernelFunction("update_task")]
    public Task UpdateTaskStatusAsync(string ticketId, string taskId, bool isCompleted)
    {
        return _apiClient.UpdateTaskStatusAsync(ticketId, taskId, isCompleted);
    }

    [KernelFunction("add_activity")]
    public Task AddActivityLogAsync(string ticketId, string message)
    {
        return _apiClient.AddActivityLogAsync(ticketId, message);
    }

    [KernelFunction("set_branch")]
    public Task SetBranchNameAsync(string ticketId, string branchName)
    {
        return _apiClient.SetBranchNameAsync(ticketId, branchName);
    }
}
