using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

public interface IKanbanApiClient
{
    Task<TicketDto?> GetTicketAsync(string ticketId);
    Task<TicketDto?> UpdateTicketStatusAsync(string ticketId, string status);
    Task<TicketDto?> AddTaskToTicketAsync(string ticketId, KanbanTask task);
    Task<TicketDto?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask);
    Task<TicketDto?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status);
    Task<TicketDto?> UpdateSubtaskRejectionAsync(string ticketId, string taskId, string subtaskId, string reason);
    Task<TicketDto?> MarkTaskCompleteAsync(string ticketId, string taskId);
    Task AddActivityLogAsync(string ticketId, string message);
    Task<TicketDto?> SetBranchNameAsync(string ticketId, string branchName);
}

public class KanbanApiClient : IKanbanApiClient
{
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _jsonOptions;

    public KanbanApiClient(string serverUrl)
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<TicketDto?> GetTicketAsync(string ticketId)
    {
        TicketDto? ticket = await _httpClient.GetFromJsonAsync<TicketDto>($"/api/tickets/{ticketId}", _jsonOptions);

        return ticket;
    }

    public async Task<TicketDto?> MarkTaskCompleteAsync(string ticketId, string taskId)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/complete", new { }, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }

    public async Task<TicketDto?> UpdateTicketStatusAsync(string ticketId, string status)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status }, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }

    public async Task<TicketDto?> AddTaskToTicketAsync(string ticketId, KanbanTask task)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks", task, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }

    public async Task<TicketDto?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks", subtask, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }

    public async Task<TicketDto?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        string encodedSubtaskId = Uri.EscapeDataString(subtaskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks/{encodedSubtaskId}", new { status }, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }

    public async Task<TicketDto?> UpdateSubtaskRejectionAsync(string ticketId, string taskId, string subtaskId, string reason)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        string encodedSubtaskId = Uri.EscapeDataString(subtaskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks/{encodedSubtaskId}/rejection", new { reason }, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }

    public async Task AddActivityLogAsync(string ticketId, string message)
    {
        await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/activity", new { message }, _jsonOptions);
    }

    public async Task<TicketDto?> SetBranchNameAsync(string ticketId, string branchName)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/branch", new { branchName }, _jsonOptions);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions);
        }

        return ticket;
    }
}
