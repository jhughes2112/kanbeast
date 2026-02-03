using System.Net.Http.Json;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

public interface IKanbanApiClient
{
    Task<TicketDto?> GetTicketAsync(string ticketId);
    Task<TicketDto?> UpdateTicketStatusAsync(string ticketId, string status);
    Task<TicketDto?> AddTaskToTicketAsync(string ticketId, KanbanTask task);
    Task<TicketDto?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status);
    Task<TicketDto?> UpdateSubtaskRejectionAsync(string ticketId, string taskId, string subtaskId, string reason);
    Task AddActivityLogAsync(string ticketId, string message);
    Task<TicketDto?> SetBranchNameAsync(string ticketId, string branchName);
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

    public async Task<TicketDto?> UpdateTicketStatusAsync(string ticketId, string status)
    {
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status });
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TicketDto>();
        }
        return null;
    }

    public async Task<TicketDto?> AddTaskToTicketAsync(string ticketId, KanbanTask task)
    {
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks", task);
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TicketDto>();
        }
        return null;
    }

    public async Task<TicketDto?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status)
    {
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{taskId}/subtasks/{subtaskId}", new { status });
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TicketDto>();
        }
        return null;
    }

    public async Task<TicketDto?> UpdateSubtaskRejectionAsync(string ticketId, string taskId, string subtaskId, string reason)
    {
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{taskId}/subtasks/{subtaskId}/rejection", new { reason });
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TicketDto>();
        }
        return null;
    }

    public async Task AddActivityLogAsync(string ticketId, string message)
    {
        await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/activity", new { message });
    }

    public async Task<TicketDto?> SetBranchNameAsync(string ticketId, string branchName)
    {
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/branch", new { branchName });
        if (response.IsSuccessStatusCode)
        {
            return await response.Content.ReadFromJsonAsync<TicketDto>();
        }
        return null;
    }
}
