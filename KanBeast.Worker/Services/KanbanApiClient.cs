using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

public interface IKanbanApiClient
{
    Task<TicketDto?> GetTicketAsync(string ticketId, CancellationToken cancellationToken);
    Task<TicketDto?> UpdateTicketStatusAsync(string ticketId, string status, CancellationToken cancellationToken);
    Task<TicketDto?> AddTaskToTicketAsync(string ticketId, KanbanTask task, CancellationToken cancellationToken);
    Task<TicketDto?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask, CancellationToken cancellationToken);
    Task<TicketDto?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status, CancellationToken cancellationToken);
    Task<TicketDto?> MarkTaskCompleteAsync(string ticketId, string taskId, CancellationToken cancellationToken);
    Task<TicketDto?> DeleteAllTasksAsync(string ticketId, CancellationToken cancellationToken);
    Task AddActivityLogAsync(string ticketId, string message, CancellationToken cancellationToken);
    Task<TicketDto?> SetBranchNameAsync(string ticketId, string branchName, CancellationToken cancellationToken);
    Task<TicketDto?> AddLlmCostAsync(string ticketId, decimal cost, CancellationToken cancellationToken);
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

    public async Task<TicketDto?> GetTicketAsync(string ticketId, CancellationToken cancellationToken)
    {
        TicketDto? ticket = await _httpClient.GetFromJsonAsync<TicketDto>($"/api/tickets/{ticketId}", _jsonOptions, cancellationToken);

        return ticket;
    }

    public async Task<TicketDto?> MarkTaskCompleteAsync(string ticketId, string taskId, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/complete", new { }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<TicketDto?> UpdateTicketStatusAsync(string ticketId, string status, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<TicketDto?> AddTaskToTicketAsync(string ticketId, KanbanTask task, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks", task, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<TicketDto?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks", subtask, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<TicketDto?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        string encodedSubtaskId = Uri.EscapeDataString(subtaskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks/{encodedSubtaskId}", new { status }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task AddActivityLogAsync(string ticketId, string message, CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/activity", new { message }, _jsonOptions, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"Failed to add activity log: {response.StatusCode} - {error}");
        }
    }

    public async Task<TicketDto?> SetBranchNameAsync(string ticketId, string branchName, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/branch", new { branchName }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<TicketDto?> AddLlmCostAsync(string ticketId, decimal cost, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/cost", new { cost }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }
        else
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"Failed to add LLM cost: {response.StatusCode} - {error}");
        }

        return ticket;
    }

    public async Task<TicketDto?> DeleteAllTasksAsync(string ticketId, CancellationToken cancellationToken)
    {
        TicketDto? ticket = null;
        HttpResponseMessage response = await _httpClient.DeleteAsync($"/api/tickets/{ticketId}/tasks", cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<TicketDto>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }
}
