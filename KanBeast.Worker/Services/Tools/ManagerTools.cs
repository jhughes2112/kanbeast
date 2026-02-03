using System.ComponentModel;
using System.Text.Json;
using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

// Tools for the manager agent to control workflow transitions
public class ManagerTools
{
    private readonly IKanbanApiClient _apiClient;
    private readonly OrchestratorState _state;
    private readonly string _ticketId;
    private AgentToolResult? _lastToolResult;

    public ManagerTools(IKanbanApiClient apiClient, OrchestratorState state, string ticketId)
    {
        _apiClient = apiClient;
        _state = state;
        _ticketId = ticketId;
    }

    public AgentToolResult? GetLastToolResult()
    {
        return _lastToolResult;
    }

    public void ClearLastToolResult()
    {
        _lastToolResult = null;
    }

    [KernelFunction("assign_to_developer")]
    [Description("Assign the current subtask to the developer agent. Transfers control to developer until they complete the work.")]
    public async Task<string> AssignToDeveloperAsync(
        [Description("Developer mode: 'implementation', 'testing', or 'write-tests'")] string mode,
        [Description("Clear description of what the developer should accomplish")] string goal,
        [Description("File paths the developer should read before making changes (JSON array)")] string filesToInspectJson,
        [Description("File paths the developer is expected to create or modify (JSON array)")] string filesToModifyJson,
        [Description("List of criteria that must be met for completion (JSON array)")] string acceptanceCriteriaJson,
        [Description("Context from previous attempts or dependent subtasks, or empty")] string priorContext,
        [Description("Rules or patterns the developer must follow (JSON array)")] string constraintsJson)
    {
        List<string> filesToInspect = ParseJsonArray(filesToInspectJson);
        List<string> filesToModify = ParseJsonArray(filesToModifyJson);
        List<string> acceptanceCriteria = ParseJsonArray(acceptanceCriteriaJson);
        List<string> constraints = ParseJsonArray(constraintsJson);

        AssignToDeveloperParams assignment = new AssignToDeveloperParams
        {
            Mode = mode,
            Goal = goal,
            FilesToInspect = filesToInspect,
            FilesToModify = filesToModify,
            AcceptanceCriteria = acceptanceCriteria,
            PriorContext = string.IsNullOrWhiteSpace(priorContext) ? null : priorContext,
            Constraints = constraints
        };

        _state.LastManagerAssignment = assignment;
        _state.CurrentDeveloperMode = mode;

        if (!string.IsNullOrEmpty(_state.CurrentSubtaskId) && !string.IsNullOrEmpty(_state.CurrentTaskId))
        {
            await _apiClient.UpdateSubtaskStatusAsync(_ticketId, _state.CurrentTaskId, _state.CurrentSubtaskId, SubtaskStatus.InProgress);
        }

        await _apiClient.AddActivityLogAsync(_ticketId, $"Manager: Assigned to developer (mode: {mode}) - {goal}");

        _lastToolResult = new AgentToolResult
        {
            ToolName = AgentToolNames.AssignToDeveloper,
            ShouldTransition = true,
            NextAgent = AgentRole.Developer,
            NextDeveloperMode = mode,
            IsTerminal = false,
            Message = $"Assigned to developer in {mode} mode"
        };

        return $"Control transferred to developer agent in {mode} mode.";
    }

    [KernelFunction("update_subtask")]
    [Description("Update the current subtask status after verification. Use after verifying developer work.")]
    public async Task<string> UpdateSubtaskAsync(
        [Description("New status: 'complete', 'rejected', or 'blocked'")] string status,
        [Description("Reason for the status change, feedback, or blocker details")] string notes)
    {
        if (string.IsNullOrEmpty(_state.CurrentSubtaskId) || string.IsNullOrEmpty(_state.CurrentTaskId))
        {
            return "Error: No current subtask to update.";
        }

        SubtaskStatus subtaskStatus = status.ToLowerInvariant() switch
        {
            "complete" => SubtaskStatus.Complete,
            "rejected" => SubtaskStatus.Rejected,
            "blocked" => SubtaskStatus.Blocked,
            _ => SubtaskStatus.Incomplete
        };

        if (subtaskStatus == SubtaskStatus.Rejected)
        {
            _state.IncrementRejectionCount(_state.CurrentSubtaskId);
            int rejectionCount = _state.GetRejectionCount(_state.CurrentSubtaskId);

            if (rejectionCount >= 3)
            {
                subtaskStatus = SubtaskStatus.Blocked;
                notes = $"Escalated to blocked after {rejectionCount} rejections. Last rejection: {notes}";
                await _apiClient.AddActivityLogAsync(_ticketId, $"Manager: Subtask escalated to blocked after {rejectionCount} rejections");
            }
            else
            {
                await _apiClient.AddActivityLogAsync(_ticketId, $"Manager: Subtask rejected ({rejectionCount}/3) - {notes}");
            }
        }
        else if (subtaskStatus == SubtaskStatus.Complete)
        {
            _state.ResetRejectionCount(_state.CurrentSubtaskId);
            await _apiClient.AddActivityLogAsync(_ticketId, $"Manager: Subtask completed - {notes}");
        }
        else if (subtaskStatus == SubtaskStatus.Blocked)
        {
            await _apiClient.AddActivityLogAsync(_ticketId, $"Manager: Subtask blocked - {notes}");
        }

        await _apiClient.UpdateSubtaskStatusAsync(_ticketId, _state.CurrentTaskId, _state.CurrentSubtaskId, subtaskStatus);

        _lastToolResult = new AgentToolResult
        {
            ToolName = AgentToolNames.UpdateSubtask,
            ShouldTransition = false,
            IsTerminal = false,
            Message = $"Subtask status updated to {status}"
        };

        return $"Subtask status updated to {status}.";
    }

    [KernelFunction("complete_ticket")]
    [Description("Mark the entire ticket as Done. Use only after all work and tests are complete.")]
    public async Task<string> CompleteTicketAsync(
        [Description("Brief summary of all changes made for this ticket")] string summary)
    {
        await _apiClient.UpdateTicketStatusAsync(_ticketId, "Done");
        await _apiClient.AddActivityLogAsync(_ticketId, $"Manager: Ticket completed - {summary}");

        _lastToolResult = new AgentToolResult
        {
            ToolName = AgentToolNames.CompleteTicket,
            ShouldTransition = false,
            IsTerminal = true,
            Message = summary
        };

        return "Ticket marked as Done.";
    }

    private static List<string> ParseJsonArray(string json)
    {
        List<string> result = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            List<string>? parsed = JsonSerializer.Deserialize<List<string>>(json);
            if (parsed != null)
            {
                result = parsed;
            }
        }
        catch
        {
            // If not valid JSON array, treat as single item or comma-separated
            if (json.Contains(','))
            {
                string[] parts = json.Split(',');
                foreach (string part in parts)
                {
                    string trimmed = part.Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(trimmed))
                    {
                        result.Add(trimmed);
                    }
                }
            }
            else
            {
                string trimmed = json.Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    result.Add(trimmed);
                }
            }
        }

        return result;
    }
}
