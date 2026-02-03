using System.Diagnostics;
using System.Text;

namespace KanBeast.Worker.Services;

public interface IToolExecutor
{
    Task<string> ExecuteBashCommandAsync(string command, string workDir);
    Task<string> ReadFileAsync(string filePath);
    Task<string> ReadFileLinesAsync(string filePath, int startLine, int endLine);
    Task WriteFileAsync(string filePath, string content);
    Task CreateFileAsync(string filePath, string content);
    Task EditFileAsync(string filePath, string oldContent, string newContent);
    Task<string> MultiEditFileAsync(string filePath, string editsJson);
    Task<string> ListFilesAsync(string directoryPath);
    Task<string> SearchFilesAsync(string directoryPath, string searchPattern, int maxResults);
    Task<bool> RemoveFileAsync(string filePath);
    Task<bool> FileExistsAsync(string filePath);
    Task<bool> DirectoryExistsAsync(string directoryPath);
}

public class ToolExecutor : IToolExecutor
{
    public async Task<string> ExecuteBashCommandAsync(string command, string workDir)
    {
        var shellPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
        var shellArgs = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command.Replace("\"", "\\\"")}\"";
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shellPath,
                Arguments = shellArgs,
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return $"Exit Code: {process.ExitCode}\nOutput:\n{output}\nError:\n{error}";
    }

    public async Task<string> ReadFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        return await File.ReadAllTextAsync(filePath);
    }

    public async Task WriteFileAsync(string filePath, string content)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(filePath, content);
    }

    public async Task EditFileAsync(string filePath, string oldContent, string newContent)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException($"File not found: {filePath}");

        var fileContent = await File.ReadAllTextAsync(filePath);

        var firstIndex = fileContent.IndexOf(oldContent, StringComparison.Ordinal);
        if (firstIndex < 0)
            throw new InvalidOperationException($"Old content not found in file: {filePath}");

        var secondIndex = fileContent.IndexOf(oldContent, firstIndex + oldContent.Length, StringComparison.Ordinal);
        if (secondIndex >= 0)
            throw new InvalidOperationException($"Old content matched multiple times in file: {filePath}");

        string updatedContent = fileContent[..firstIndex] + newContent + fileContent[(firstIndex + oldContent.Length)..];
        await File.WriteAllTextAsync(filePath, updatedContent);
    }

    public async Task<string> MultiEditFileAsync(string filePath, string editsJson)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        EditOperation[]? edits;
        try
        {
            edits = System.Text.Json.JsonSerializer.Deserialize<EditOperation[]>(editsJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON format for edits: {ex.Message}");
        }

        if (edits == null || edits.Length == 0)
        {
            return "No edits provided.";
        }

        string fileContent = await File.ReadAllTextAsync(filePath);
        int successCount = 0;
        List<string> failures = new List<string>();

        foreach (EditOperation edit in edits)
        {
            if (string.IsNullOrEmpty(edit.OldContent))
            {
                failures.Add($"Edit {successCount + failures.Count + 1}: oldContent is empty");
                continue;
            }

            int firstIndex = fileContent.IndexOf(edit.OldContent, StringComparison.Ordinal);
            if (firstIndex < 0)
            {
                failures.Add($"Edit {successCount + failures.Count + 1}: oldContent not found");
                continue;
            }

            int secondIndex = fileContent.IndexOf(edit.OldContent, firstIndex + edit.OldContent.Length, StringComparison.Ordinal);
            if (secondIndex >= 0)
            {
                failures.Add($"Edit {successCount + failures.Count + 1}: oldContent matched multiple times");
                continue;
            }

            fileContent = fileContent[..firstIndex] + (edit.NewContent ?? string.Empty) + fileContent[(firstIndex + edit.OldContent.Length)..];
            successCount++;
        }

        await File.WriteAllTextAsync(filePath, fileContent);

        if (failures.Count == 0)
        {
            return $"All {successCount} edits applied successfully.";
        }
        else
        {
            return $"{successCount} edits applied, {failures.Count} failed:\n" + string.Join("\n", failures);
        }
    }

    public Task<string> ListFilesAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        StringBuilder result = new StringBuilder();
        foreach (string entry in Directory.EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly))
        {
            string? name = Path.GetFileName(entry);
            if (!string.IsNullOrEmpty(name))
            {
                result.AppendLine(name);
            }
        }

        return Task.FromResult(result.ToString().TrimEnd());
    }

    public Task<string> ReadFileLinesAsync(string filePath, int startLine, int endLine)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        if (startLine < 1)
        {
            startLine = 1;
        }

        if (endLine < startLine)
        {
            endLine = startLine;
        }

        string[] allLines = File.ReadAllLines(filePath);
        int maxLine = allLines.Length;

        if (startLine > maxLine)
        {
            return Task.FromResult(string.Empty);
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

        return Task.FromResult(result.ToString().TrimEnd());
    }

    public Task CreateFileAsync(string filePath, string content)
    {
        if (File.Exists(filePath))
        {
            throw new InvalidOperationException($"File already exists: {filePath}. Use write_file to overwrite.");
        }

        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(filePath, content);
        return Task.CompletedTask;
    }

    public Task<string> SearchFilesAsync(string directoryPath, string searchPattern, int maxResults)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");
        }

        if (maxResults <= 0)
        {
            maxResults = 50;
        }

        StringBuilder result = new StringBuilder();
        int count = 0;

        foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(directoryPath, file);
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

        return Task.FromResult(result.ToString().TrimEnd());
    }

    public Task<bool> RemoveFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return Task.FromResult(false);
        }

        File.Delete(filePath);
        return Task.FromResult(true);
    }

    public Task<bool> FileExistsAsync(string filePath)
    {
        return Task.FromResult(File.Exists(filePath));
    }

    public Task<bool> DirectoryExistsAsync(string directoryPath)
    {
        return Task.FromResult(Directory.Exists(directoryPath));
    }
}

public class EditOperation
{
    public string OldContent { get; set; } = string.Empty;
    public string NewContent { get; set; } = string.Empty;
}
