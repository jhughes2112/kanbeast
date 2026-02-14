using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Shared;

namespace KanBeast.Worker.Services;

public interface IKanbanApiClient
{
    Task<Ticket?> GetTicketAsync(string ticketId, CancellationToken cancellationToken);
    Task<Ticket?> UpdateTicketStatusAsync(string ticketId, string status, CancellationToken cancellationToken);
    Task<Ticket?> AddTaskToTicketAsync(string ticketId, KanbanTask task, CancellationToken cancellationToken);
    Task<Ticket?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask, CancellationToken cancellationToken);
    Task<Ticket?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status, CancellationToken cancellationToken);
    Task<Ticket?> MarkTaskCompleteAsync(string ticketId, string taskId, CancellationToken cancellationToken);
    Task<Ticket?> DeleteAllTasksAsync(string ticketId, CancellationToken cancellationToken);
    Task AddActivityLogAsync(string ticketId, string message, CancellationToken cancellationToken);
    Task<Ticket?> SetBranchNameAsync(string ticketId, string branchName, CancellationToken cancellationToken);
    Task<Ticket?> AddLlmCostAsync(string ticketId, decimal cost, CancellationToken cancellationToken);
    Task<ConversationData?> GetPlanningConversationAsync(string ticketId, CancellationToken cancellationToken);
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

    public async Task<Ticket?> GetTicketAsync(string ticketId, CancellationToken cancellationToken)
    {
        Ticket? ticket = await _httpClient.GetFromJsonAsync<Ticket>($"/api/tickets/{ticketId}", _jsonOptions, cancellationToken);

        return ticket;
    }

    public async Task<Ticket?> MarkTaskCompleteAsync(string ticketId, string taskId, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/complete", new { }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<Ticket?> UpdateTicketStatusAsync(string ticketId, string status, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/status", new { status }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<Ticket?> AddTaskToTicketAsync(string ticketId, KanbanTask task, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks", task, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<Ticket?> AddSubtaskToTaskAsync(string ticketId, string taskId, KanbanSubtask subtask, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks", subtask, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<Ticket?> UpdateSubtaskStatusAsync(string ticketId, string taskId, string subtaskId, SubtaskStatus status, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        string encodedTaskId = Uri.EscapeDataString(taskId);
        string encodedSubtaskId = Uri.EscapeDataString(subtaskId);
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/tasks/{encodedTaskId}/subtasks/{encodedSubtaskId}", new { status }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
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

    public async Task<Ticket?> SetBranchNameAsync(string ticketId, string branchName, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/branch", new { branchName }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<Ticket?> AddLlmCostAsync(string ticketId, decimal cost, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        HttpResponseMessage response = await _httpClient.PatchAsJsonAsync($"/api/tickets/{ticketId}/cost", new { cost }, _jsonOptions, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }
        else
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"Failed to add LLM cost: {response.StatusCode} - {error}");
        }

        return ticket;
    }

    public async Task<Ticket?> DeleteAllTasksAsync(string ticketId, CancellationToken cancellationToken)
    {
        Ticket? ticket = null;
        HttpResponseMessage response = await _httpClient.DeleteAsync($"/api/tickets/{ticketId}/tasks", cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            ticket = await response.Content.ReadFromJsonAsync<Ticket>(_jsonOptions, cancellationToken);
        }

        return ticket;
    }

    public async Task<ConversationData?> GetPlanningConversationAsync(string ticketId, CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.GetFromJsonAsync<ConversationData>($"/api/tickets/{ticketId}/conversations/planning", _jsonOptions, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
