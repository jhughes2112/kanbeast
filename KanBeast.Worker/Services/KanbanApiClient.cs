using System.Net.Http.Json;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

public interface IKanbanApiClient
{
    Task<TicketDto?> GetTicketAsync(string ticketId);
    Task UpdateTicketStatusAsync(string ticketId, string status);
    Task AddTaskAsync(string ticketId, KanbanTaskDto task);
    Task UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status);
    Task AddActivityLogAsync(string ticketId, string message);
    Task SetBranchNameAsync(string ticketId, string branchName);
}

public class KanbanApiClient : IKanbanApiClient
{
    private readonly HttpClient _httpClient;

    public KanbanApiClient(string serverUrl)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
    }

    public async Task<TicketDto?> GetTicketAsync(string ticketId)
    {
        return await _httpClient.GetFromJsonAsync<TicketDto>($"/api/tickets/{ticketId}");
    }

    public async Task UpdateTicketStatusAsync(string ticketId, string status)
    {
        await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status });
    }

    public async Task AddTaskAsync(string ticketId, KanbanTaskDto task)
    {
        await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks", task);
    }

    public async Task UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status)
    {
        await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{taskId}/subtasks/{subtaskId}", new { status });
    }

    public async Task AddActivityLogAsync(string ticketId, string message)
    {
        await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/activity", new { message });
    }

    public async Task SetBranchNameAsync(string ticketId, string branchName)
    {
        await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/branch", new { branchName });
    }
}
