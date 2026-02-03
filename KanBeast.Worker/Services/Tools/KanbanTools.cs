using KanBeast.Worker.Models;
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
    public Task AddTaskAsync(string ticketId, KanbanTaskDto task)
    {
        return _apiClient.AddTaskAsync(ticketId, task);
    }

    [KernelFunction("update_subtask")]
    public Task UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status)
    {
        return _apiClient.UpdateSubtaskStatusAsync(ticketId, taskId, subtaskId, status);
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
