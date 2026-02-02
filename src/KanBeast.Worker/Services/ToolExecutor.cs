using System.Diagnostics;
using System.Text;

namespace KanBeast.Worker.Services;

public interface IToolExecutor
{
    Task<string> ExecuteBashCommandAsync(string command, string workDir);
    Task<string> ReadFileAsync(string filePath);
    Task WriteFileAsync(string filePath, string content);
    Task EditFileAsync(string filePath, string oldContent, string newContent);
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
        
        if (!fileContent.Contains(oldContent))
            throw new InvalidOperationException($"Old content not found in file: {filePath}");

        var updatedContent = fileContent.Replace(oldContent, newContent);
        await File.WriteAllTextAsync(filePath, updatedContent);
    }
}
