using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to read and write files.
// Default CWD is the git repository folder.
public class FileTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly string _workDir;
    private readonly Dictionary<LlmRole, List<Tool>> _toolsByRole;

    public FileTools(string workDir)
    {
        _workDir = workDir;
        _toolsByRole = BuildToolsByRole();
    }

    private string ResolvePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        return Path.GetFullPath(Path.Combine(_workDir, path));
    }

    private Dictionary<LlmRole, List<Tool>> BuildToolsByRole()
    {
        List<Tool> readOnlyTools = new List<Tool>();
        ToolHelper.AddTools(readOnlyTools, this,
            nameof(ReadFileAsync),
            nameof(GetFileAsync));

        List<Tool> developerTools = new List<Tool>();
        ToolHelper.AddTools(developerTools, this,
            nameof(ReadFileAsync),
            nameof(GetFileAsync),
            nameof(WriteFileAsync),
            nameof(EditFileAsync));

        Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
        {
            [LlmRole.ManagerPlanning] = readOnlyTools,
            [LlmRole.ManagerImplementing] = readOnlyTools,
            [LlmRole.Developer] = developerTools,
            [LlmRole.Compaction] = new List<Tool>()
        };

        return result;
    }

    public void AddTools(List<Tool> tools, LlmRole role)
    {
        if (_toolsByRole.TryGetValue(role, out List<Tool>? roleTools))
        {
            tools.AddRange(roleTools);
        }
        else
        {
            throw new ArgumentException($"Unhandled role: {role}");
        }
    }

    [Description("Read the contents of a file.")]
    public async Task<string> ReadFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath)
    {
        return await ReadFileContentAsync(filePath);
    }

    [Description("Get the contents of a file.")]
    public async Task<string> GetFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath)
    {
        return await ReadFileContentAsync(filePath);
    }

    private async Task<string> ReadFileContentAsync(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: Path cannot be empty";
        }

        string fullPath = ResolvePath(filePath);

        try
        {
            if (!File.Exists(fullPath))
            {
                return $"Error: File not found: {filePath}";
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            string content = await File.ReadAllTextAsync(fullPath, cts.Token);

            return content;
        }
        catch (TaskCanceledException)
        {
            return $"Error: Timed out reading file: {filePath}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to read file: {ex.Message}";
        }
    }

    [Description("Write content to a file, creating or overwriting as needed.")]
    public async Task<string> WriteFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath,
        [Description("Content to write")] string content)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: Path cannot be empty";
        }

        string fullPath = ResolvePath(filePath);

        try
        {
            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            await File.WriteAllTextAsync(fullPath, content ?? string.Empty, cts.Token);

            return $"File written: {filePath}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Timed out writing file: {filePath}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to write file: {ex.Message}";
        }
    }

    [Description("Replace a single exact block of text in a file. oldContent must match exactly once.")]
    public async Task<string> EditFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath,
        [Description("Exact text to find and replace")] string oldContent,
        [Description("Replacement text")] string newContent)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return "Error: Path cannot be empty";
        }

        string fullPath = ResolvePath(filePath);

        if (string.IsNullOrEmpty(oldContent))
        {
            return "Error: oldContent cannot be empty";
        }

        try
        {
            if (!File.Exists(fullPath))
            {
                return $"Error: File not found: {filePath}";
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);

            int firstIndex = fileContent.IndexOf(oldContent, StringComparison.Ordinal);
            if (firstIndex < 0)
            {
                return $"Error: oldContent not found in file: {filePath}";
            }

            int secondIndex = fileContent.IndexOf(oldContent, firstIndex + oldContent.Length, StringComparison.Ordinal);
            if (secondIndex >= 0)
            {
                return $"Error: oldContent matched multiple times in file. Include more context to make it unique.";
            }

            string updatedContent = fileContent[..firstIndex] + (newContent ?? string.Empty) + fileContent[(firstIndex + oldContent.Length)..];
            await File.WriteAllTextAsync(fullPath, updatedContent, cts.Token);

            return $"File edited: {filePath}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Timed out editing file: {filePath}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to edit file: {ex.Message}";
        }
    }
}
