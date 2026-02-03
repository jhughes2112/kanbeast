namespace KanBeast.Worker.Models;

// Parameters for the assign_to_developer tool
public class AssignToDeveloperParams
{
    public required string Mode { get; set; }
    public required string Goal { get; set; }
    public List<string> FilesToInspect { get; set; } = new List<string>();
    public List<string> FilesToModify { get; set; } = new List<string>();
    public List<string> AcceptanceCriteria { get; set; } = new List<string>();
    public string? PriorContext { get; set; }
    public List<string> Constraints { get; set; } = new List<string>();
}

// Parameters for the subtask_complete tool
public class SubtaskCompleteParams
{
    public required string Status { get; set; }
    public List<FileChangeInfo> FilesChanged { get; set; } = new List<FileChangeInfo>();
    public required string BuildStatus { get; set; }
    public TestResultInfo? TestResults { get; set; }
    public required string Message { get; set; }
    public BlockerInfo? BlockerDetails { get; set; }
}

public class FileChangeInfo
{
    public required string Path { get; set; }
    public required string Summary { get; set; }
}

public class TestResultInfo
{
    public int Total { get; set; }
    public int Passed { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
}

public class BlockerInfo
{
    public required string Issue { get; set; }
    public List<string> Tried { get; set; } = new List<string>();
    public required string Needed { get; set; }
}

// Parameters for the update_subtask tool
public class UpdateSubtaskParams
{
    public required string Status { get; set; }
    public required string Notes { get; set; }
}

// Parameters for the complete_ticket tool
public class CompleteTicketParams
{
    public required string Summary { get; set; }
}

// Developer modes
public static class DeveloperMode
{
    public const string Implementation = "implementation";
    public const string Testing = "testing";
    public const string WriteTests = "write-tests";
}

// Subtask completion status values
public static class SubtaskCompleteStatus
{
    public const string Complete = "complete";
    public const string Blocked = "blocked";
}

// Update subtask status values for manager
public static class SubtaskUpdateStatus
{
    public const string Complete = "complete";
    public const string Rejected = "rejected";
    public const string Blocked = "blocked";
}
