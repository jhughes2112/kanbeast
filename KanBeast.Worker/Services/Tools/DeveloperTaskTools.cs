using System.ComponentModel;
using System.Text.Json;
using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

// Tools for the developer agent to signal work completion
public class DeveloperTaskTools
{
    private readonly IKanbanApiClient _apiClient;
    private readonly OrchestratorState _state;
    private readonly string _ticketId;
    private AgentToolResult? _lastToolResult;

    public DeveloperTaskTools(IKanbanApiClient apiClient, OrchestratorState state, string ticketId)
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

    [KernelFunction("subtask_complete")]
    [Description("Signal completion of current work and return control to the manager agent. Must be called when work is done or blocked.")]
    public async Task<string> SubtaskCompleteAsync(
        [Description("Status: 'complete' if work is done, 'blocked' if unable to proceed")] string status,
        [Description("JSON array of file changes: [{\"path\": \"...\", \"summary\": \"...\"}]")] string filesChangedJson,
        [Description("Build status: 'pass' or 'fail'")] string buildStatus,
        [Description("Summary of what was done or why blocked")] string message,
        [Description("JSON object with blocker details if blocked: {\"issue\": \"...\", \"tried\": [...], \"needed\": \"...\"}, or empty if not blocked")] string blockerDetailsJson,
        [Description("JSON object with test results if applicable: {\"total\": N, \"passed\": N, \"failed\": N, \"skipped\": N}, or empty")] string testResultsJson)
    {
        List<FileChangeInfo> filesChanged = ParseFilesChanged(filesChangedJson);
        BlockerInfo? blockerDetails = ParseBlockerDetails(blockerDetailsJson);
        TestResultInfo? testResults = ParseTestResults(testResultsJson);

        SubtaskCompleteParams result = new SubtaskCompleteParams
        {
            Status = status,
            FilesChanged = filesChanged,
            BuildStatus = buildStatus,
            Message = message,
            BlockerDetails = blockerDetails,
            TestResults = testResults
        };

        _state.LastDeveloperResult = result;

        string statusDescription = status.ToLowerInvariant() == SubtaskCompleteStatus.Complete
            ? "completed"
            : "blocked";

        await _apiClient.AddActivityLogAsync(_ticketId, $"Developer: Work {statusDescription} - {message}");

        if (!string.IsNullOrEmpty(_state.CurrentSubtaskId) && !string.IsNullOrEmpty(_state.CurrentTaskId))
        {
            SubtaskStatus subtaskStatus = status.ToLowerInvariant() == SubtaskCompleteStatus.Complete
                ? SubtaskStatus.AwaitingReview
                : SubtaskStatus.Blocked;

            await _apiClient.UpdateSubtaskStatusAsync(_ticketId, _state.CurrentTaskId, _state.CurrentSubtaskId, subtaskStatus);
        }

        _lastToolResult = new AgentToolResult
        {
            ToolName = AgentToolNames.SubtaskComplete,
            ShouldTransition = true,
            NextAgent = AgentRole.Manager,
            IsTerminal = false,
            Message = message
        };

        return $"Work {statusDescription}. Control returned to manager.";
    }

    private static List<FileChangeInfo> ParseFilesChanged(string json)
    {
        List<FileChangeInfo> result = new List<FileChangeInfo>();

        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            List<FileChangeInfo>? parsed = JsonSerializer.Deserialize<List<FileChangeInfo>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (parsed != null)
            {
                result = parsed;
            }
        }
        catch
        {
            // Ignore parse errors, return empty list
        }

        return result;
    }

    private static BlockerInfo? ParseBlockerDetails(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            BlockerInfo? parsed = JsonSerializer.Deserialize<BlockerInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static TestResultInfo? ParseTestResults(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            TestResultInfo? parsed = JsonSerializer.Deserialize<TestResultInfo>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed;
        }
        catch
        {
            return null;
        }
    }
}
