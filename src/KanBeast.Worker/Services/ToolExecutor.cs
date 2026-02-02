using System.Diagnostics;
using System.Text;

namespace KanBeast.Worker.Services;

public interface IToolExecutor
{
    Task<string> ExecuteBashCommandAsync(string command, string workDir);
    Task<string> ReadFileAsync(string filePath);
    Task WriteFileAsync(string filePath, string content);
    Task EditFileAsync(string filePath, string oldContent, string newContent);
    Task<string> PatchFileAsync(string workDir, string patch);
    Task<string> ListFilesAsync(string directoryPath);
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

        var updatedContent = fileContent[..firstIndex] + newContent + fileContent[(firstIndex + oldContent.Length)..];
        await File.WriteAllTextAsync(filePath, updatedContent);
    }

    public async Task<string> PatchFileAsync(string workDir, string patch)
    {
        if (!Directory.Exists(workDir))
            throw new DirectoryNotFoundException($"Directory not found: {workDir}");

        var patchFile = Path.Combine(Path.GetTempPath(), $"kanbeast_patch_{Guid.NewGuid():N}.patch");
        await File.WriteAllTextAsync(patchFile, patch);

        try
        {
            var result = await ExecuteBashCommandAsync($"git apply --whitespace=nowarn \"{patchFile}\"", workDir);
            return result;
        }
        finally
        {
            if (File.Exists(patchFile))
            {
                File.Delete(patchFile);
            }
        }
    }

    public Task<string> ListFilesAsync(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"Directory not found: {directoryPath}");

        var entries = Directory
            .EnumerateFileSystemEntries(directoryPath, "*", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name));

        return Task.FromResult(string.Join('\n', entries));
    }
}
