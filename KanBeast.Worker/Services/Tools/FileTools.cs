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
            [LlmRole.Planning] = readOnlyTools,
            [LlmRole.QA] = readOnlyTools,
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
    public async Task<ToolResult> ReadFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath,
        [Description("0-based line index to start reading from (default: 0 for start of file)")] string offset,
        [Description("Number of lines to read forward from offset (default: all remaining lines)")] string lines,
        CancellationToken cancellationToken)
    {
        return await ReadFileContentAsync(filePath, offset, lines, cancellationToken);
    }

    [Description("Get the contents of a file.")]
    public async Task<ToolResult> GetFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath,
        [Description("0-based line index to start reading from (default: 0 for start of file)")] string offset,
        [Description("Number of lines to read forward from offset (default: all remaining lines)")] string lines,
        CancellationToken cancellationToken)
    {
        return await ReadFileContentAsync(filePath, offset, lines, cancellationToken);
    }

    private async Task<ToolResult> ReadFileContentAsync(string filePath, string offset, string lines, CancellationToken cancellationToken)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            result = new ToolResult("Error: Path cannot be empty");
        }
        else
        {
            string fullPath = ResolvePath(filePath);

            try
            {
                if (!File.Exists(fullPath))
                {
                    result = new ToolResult($"Error: File not found: {filePath}");
                }
                else
                {
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(DefaultTimeout);
                    string content = await File.ReadAllTextAsync(fullPath, cts.Token);

                    int offsetValue = 0;
                    bool offsetValid = string.IsNullOrWhiteSpace(offset) || int.TryParse(offset, out offsetValue);

                    int linesValue = 0;
                    bool linesValid = string.IsNullOrWhiteSpace(lines) || int.TryParse(lines, out linesValue);

                    if (!offsetValid)
                    {
                        result = new ToolResult($"Error: Invalid offset value: {offset}");
                    }
                    else if (!linesValid)
                    {
                        result = new ToolResult($"Error: Invalid lines value: {lines}");
                    }
                    else if (offsetValue < 0)
                    {
                        result = new ToolResult("Error: offset must be >= 0");
                    }
                    else if (linesValue < 0)
                    {
                        result = new ToolResult("Error: lines must be >= 0");
                    }
                    else if (offsetValue == 0 && linesValue == 0)
                    {
                        result = new ToolResult(content);
                    }
                    else
                    {
                        string[] allLines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                        int totalLines = allLines.Length;

                        if (totalLines == 0)
                        {
                            result = new ToolResult($"File is empty: {filePath}");
                        }
                        else
                        {
                            int startIndex = Math.Min(offsetValue, totalLines - 1);
                            int count = linesValue > 0 ? linesValue : totalLines - startIndex;
                            int endIndex = Math.Min(startIndex + count - 1, totalLines - 1);

                            StringBuilder sb = new StringBuilder();
                            sb.AppendLine($"Lines {startIndex + 1}-{endIndex + 1} of {totalLines} from {filePath}:");

                            for (int i = startIndex; i <= endIndex; i++)
                            {
                                sb.AppendLine($"{i + 1}: {allLines[i]}");
                            }

                            result = new ToolResult(sb.ToString());
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Timed out or cancelled reading file: {filePath}");
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to read file: {ex.Message}");
            }
        }

        return result;
    }

    [Description("Write content to a file, creating or overwriting as needed.")]
    public async Task<ToolResult> WriteFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath,
        [Description("Content to write")] string content,
        CancellationToken cancellationToken)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            result = new ToolResult("Error: Path cannot be empty");
        }
        else
        {
            string fullPath = ResolvePath(filePath);

            try
            {
                string? directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(DefaultTimeout);
                await File.WriteAllTextAsync(fullPath, content ?? string.Empty, cts.Token);

                result = new ToolResult($"File written: {filePath}");
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Timed out or cancelled writing file: {filePath}");
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to write file: {ex.Message}");
            }
        }

        return result;
    }

    [Description("Replace a single exact block of text in a file. oldContent must match exactly once.")]
    public async Task<ToolResult> EditFileAsync(
        [Description("Path to the file (absolute or relative to repository)")] string filePath,
        [Description("Exact text to find and replace")] string oldContent,
        [Description("Replacement text")] string newContent,
        CancellationToken cancellationToken)
    {
        ToolResult result;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            result = new ToolResult("Error: Path cannot be empty");
        }
        else if (string.IsNullOrEmpty(oldContent))
        {
            result = new ToolResult("Error: oldContent cannot be empty");
        }
        else
        {
            string fullPath = ResolvePath(filePath);

            try
            {
                if (!File.Exists(fullPath))
                {
                    result = new ToolResult($"Error: File not found: {filePath}");
                }
                else
                {
                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(DefaultTimeout);
                    string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);

                    int firstIndex = fileContent.IndexOf(oldContent, StringComparison.Ordinal);
                    if (firstIndex < 0)
                    {
                        result = new ToolResult($"Error: oldContent not found in file: {filePath}");
                    }
                    else
                    {
                        int secondIndex = fileContent.IndexOf(oldContent, firstIndex + oldContent.Length, StringComparison.Ordinal);
                        if (secondIndex >= 0)
                        {
                            result = new ToolResult("Error: oldContent matched multiple times in file. Include more context to make it unique.");
                        }
                        else
                        {
                            string updatedContent = fileContent[..firstIndex] + (newContent ?? string.Empty) + fileContent[(firstIndex + oldContent.Length)..];
                            await File.WriteAllTextAsync(fullPath, updatedContent, cts.Token);
                            result = new ToolResult($"File edited: {filePath}");
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Timed out or cancelled editing file: {filePath}");
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to edit file: {ex.Message}");
            }
        }

        return result;
    }
}
