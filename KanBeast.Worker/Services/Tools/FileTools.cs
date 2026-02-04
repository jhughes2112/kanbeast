using System.ComponentModel;
using System.Text;
using System.Text.Json;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to read and write files within allowed directories.
public class FileTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private static readonly string[] AllowedPrefixes = { "/app", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };

    private readonly string _workDir;

    public FileTools(string workDir)
    {
        _workDir = workDir;
    }

    public Dictionary<string, ProviderTool> GetTools(LlmRole role)
    {
        Dictionary<string, ProviderTool> tools = new Dictionary<string, ProviderTool>();

        // Both Manager and Developer get file access
        ToolHelper.AddTools(tools, this,
            nameof(ReadFileAsync),
            nameof(ReadFileLinesAsync),
            nameof(SearchInFileAsync),
            nameof(WriteFileAsync),
            nameof(CreateFileAsync),
            nameof(EditFileAsync),
            nameof(ListFilesAsync),
            nameof(SearchFilesAsync),
            nameof(FileExistsAsync),
            nameof(RemoveFileAsync));

        return tools;
    }

    // Validates that a path is within allowed directories.
    private string? ValidatePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Error: Path cannot be empty";
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(_workDir, path));
        }
        catch (Exception ex)
        {
            return $"Error: Invalid path: {ex.Message}";
        }

        bool allowed = false;
        foreach (string prefix in AllowedPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix) && fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed && fullPath.StartsWith(_workDir, StringComparison.OrdinalIgnoreCase))
        {
            allowed = true;
        }

        if (!allowed)
        {
            return $"Error: Access denied. Path must be within working directory or /app";
        }

        return null;
    }

    private string GetFullPath(string path)
    {
        return Path.GetFullPath(Path.Combine(_workDir, path));
    }

    [Description("Read the entire contents of a file.")]
    public async Task<string> ReadFileAsync(
        [Description("Path to the file")] string filePath)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return error;
        }

        try
        {
            string fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return $"Error: File not found: {filePath}";
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            string content = await File.ReadAllTextAsync(fullPath, cts.Token);

            if (content.Length > 100000)
            {
                return content.Substring(0, 100000) + "\n\n[Content truncated at 100000 characters. Use read_file_lines for specific sections.]";
            }

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

    [Description("Read specific line ranges from a file with line number prefixes. Lines are 1-based.")]
    public async Task<string> ReadFileLinesAsync(
        [Description("Path to the file")] string filePath,
        [Description("Starting line number (1-based)")] int startLine,
        [Description("Ending line number (inclusive)")] int endLine)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return error;
        }

        try
        {
            string fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return $"Error: File not found: {filePath}";
            }

            if (startLine < 1)
            {
                startLine = 1;
            }

            if (endLine < startLine)
            {
                endLine = startLine;
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            string[] allLines = await File.ReadAllLinesAsync(fullPath, cts.Token);
            int maxLine = allLines.Length;

            if (startLine > maxLine)
            {
                return $"Error: Start line {startLine} exceeds file length of {maxLine} lines";
            }

            if (endLine > maxLine)
            {
                endLine = maxLine;
            }

            StringBuilder result = new StringBuilder();
            for (int i = startLine - 1; i < endLine; i++)
            {
                result.AppendLine($"{i + 1}: {allLines[i]}");
            }

            return result.ToString().TrimEnd();
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

    [Description("Search for a pattern in a file and return matching lines with context. Returns line numbers and surrounding lines.")]
    public async Task<string> SearchInFileAsync(
        [Description("Path to the file")] string filePath,
        [Description("Text pattern to search for (case-insensitive)")] string pattern,
        [Description("Number of context lines above and below each match")] int contextLines)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return "Error: Search pattern cannot be empty";
        }

        if (contextLines < 0)
        {
            contextLines = 0;
        }

        if (contextLines > 20)
        {
            contextLines = 20;
        }

        try
        {
            string fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return $"Error: File not found: {filePath}";
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            string[] allLines = await File.ReadAllLinesAsync(fullPath, cts.Token);

            List<int> matchingLines = new List<int>();
            for (int i = 0; i < allLines.Length; i++)
            {
                if (allLines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase))
                {
                    matchingLines.Add(i);
                }
            }

            if (matchingLines.Count == 0)
            {
                return $"No matches found for: {pattern}";
            }

            HashSet<int> linesToInclude = new HashSet<int>();
            foreach (int matchLine in matchingLines)
            {
                int start = Math.Max(0, matchLine - contextLines);
                int end = Math.Min(allLines.Length - 1, matchLine + contextLines);
                for (int i = start; i <= end; i++)
                {
                    linesToInclude.Add(i);
                }
            }

            StringBuilder result = new StringBuilder();
            result.AppendLine($"Found {matchingLines.Count} match(es) for '{pattern}':");
            result.AppendLine();

            List<int> sortedLines = linesToInclude.OrderBy(x => x).ToList();
            int lastLine = -2;

            foreach (int lineIndex in sortedLines)
            {
                if (lastLine >= 0 && lineIndex > lastLine + 1)
                {
                    result.AppendLine("...");
                }

                string marker = matchingLines.Contains(lineIndex) ? ">" : " ";
                result.AppendLine($"{marker}{lineIndex + 1}: {allLines[lineIndex]}");
                lastLine = lineIndex;
            }

            string output = result.ToString().TrimEnd();
            if (output.Length > 50000)
            {
                output = output.Substring(0, 50000) + "\n[Output truncated - too many matches. Use a more specific pattern.]";
            }

            return output;
        }
        catch (TaskCanceledException)
        {
            return $"Error: Timed out searching file: {filePath}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to search file: {ex.Message}";
        }
    }

    [Description("Write content to a file, creating or overwriting as needed.")]
    public async Task<string> WriteFileAsync(
        [Description("Path to the file")] string filePath,
        [Description("Content to write")] string content)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return error;
        }

        try
        {
            string fullPath = GetFullPath(filePath);
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

    [Description("Create a new file. Fails if file already exists.")]
    public async Task<string> CreateFileAsync(
        [Description("Path to the file")] string filePath,
        [Description("Content to write")] string content)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return error;
        }

        try
        {
            string fullPath = GetFullPath(filePath);
            if (File.Exists(fullPath))
            {
                return $"Error: File already exists: {filePath}. Use write_file to overwrite.";
            }

            string? directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);
            await File.WriteAllTextAsync(fullPath, content ?? string.Empty, cts.Token);

            return $"File created: {filePath}";
        }
        catch (TaskCanceledException)
        {
            return $"Error: Timed out creating file: {filePath}";
        }
        catch (Exception ex)
        {
            return $"Error: Failed to create file: {ex.Message}";
        }
    }

    [Description("Replace a single exact block of text in a file. oldContent must match exactly once.")]
    public async Task<string> EditFileAsync(
        [Description("Path to the file")] string filePath,
        [Description("Exact text to find and replace")] string oldContent,
        [Description("Replacement text")] string newContent)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return error;
        }

        if (string.IsNullOrEmpty(oldContent))
        {
            return "Error: oldContent cannot be empty";
        }

        try
        {
            string fullPath = GetFullPath(filePath);
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

    [Description("List files and directories in a directory.")]
    public Task<string> ListFilesAsync(
        [Description("Path to the directory")] string directoryPath)
    {
        string? error = ValidatePath(directoryPath);
        if (error != null)
        {
            return Task.FromResult(error);
        }

        try
        {
            string fullPath = GetFullPath(directoryPath);
            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult($"Error: Directory not found: {directoryPath}");
            }

            StringBuilder result = new StringBuilder();
            foreach (string entry in Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.TopDirectoryOnly))
            {
                string? name = Path.GetFileName(entry);
                if (!string.IsNullOrEmpty(name))
                {
                    result.AppendLine(name);
                }
            }

            string output = result.ToString().TrimEnd();
            if (string.IsNullOrEmpty(output))
            {
                return Task.FromResult("Directory is empty");
            }

            return Task.FromResult(output);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: Failed to list directory: {ex.Message}");
        }
    }

    [Description("Search for files by name pattern. Returns matching paths.")]
    public Task<string> SearchFilesAsync(
        [Description("Directory to search in")] string directoryPath,
        [Description("Pattern to search for in file names")] string searchPattern,
        [Description("Maximum results to return")] int maxResults)
    {
        string? error = ValidatePath(directoryPath);
        if (error != null)
        {
            return Task.FromResult(error);
        }

        if (string.IsNullOrWhiteSpace(searchPattern))
        {
            return Task.FromResult("Error: Search pattern cannot be empty");
        }

        try
        {
            string fullPath = GetFullPath(directoryPath);
            if (!Directory.Exists(fullPath))
            {
                return Task.FromResult($"Error: Directory not found: {directoryPath}");
            }

            if (maxResults <= 0)
            {
                maxResults = 50;
            }

            StringBuilder result = new StringBuilder();
            int count = 0;

            foreach (string file in Directory.EnumerateFiles(fullPath, "*", SearchOption.AllDirectories))
            {
                string relativePath = Path.GetRelativePath(fullPath, file);
                if (relativePath.Contains(searchPattern, StringComparison.OrdinalIgnoreCase))
                {
                    result.AppendLine(relativePath);
                    count++;
                    if (count >= maxResults)
                    {
                        break;
                    }
                }
            }

            string output = result.ToString().TrimEnd();
            if (string.IsNullOrEmpty(output))
            {
                return Task.FromResult($"No files found matching: {searchPattern}");
            }

            return Task.FromResult(output);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: Failed to search files: {ex.Message}");
        }
    }

    [Description("Check if a file exists.")]
    public Task<string> FileExistsAsync(
        [Description("Path to the file")] string filePath)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return Task.FromResult(error);
        }

        string fullPath = GetFullPath(filePath);
        bool exists = File.Exists(fullPath);
        return Task.FromResult(exists ? "true" : "false");
    }

    [Description("Delete a file.")]
    public Task<string> RemoveFileAsync(
        [Description("Path to the file")] string filePath)
    {
        string? error = ValidatePath(filePath);
        if (error != null)
        {
            return Task.FromResult(error);
        }

        try
        {
            string fullPath = GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return Task.FromResult($"Error: File not found: {filePath}");
            }

            File.Delete(fullPath);
            return Task.FromResult($"File deleted: {filePath}");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"Error: Failed to delete file: {ex.Message}");
        }
    }
}
