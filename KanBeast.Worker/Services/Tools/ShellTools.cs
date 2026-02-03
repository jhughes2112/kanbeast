using System.ComponentModel;
using System.Diagnostics;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to execute shell commands within allowed directories.
public class ShellTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    private static readonly string[] AllowedPrefixes = { "/app", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };

    private readonly string _workDir;

    public ShellTools(string workDir)
    {
        _workDir = workDir;
    }

    // Validates that a working directory is within allowed paths.
    private string? ValidateWorkDir(string workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir))
        {
            return "Error: Working directory cannot be empty";
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(workDir);
        }
        catch (Exception ex)
        {
            return $"Error: Invalid working directory: {ex.Message}";
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
            return $"Error: Access denied. Working directory must be within allowed paths.";
        }

        if (!Directory.Exists(fullPath))
        {
            return $"Error: Working directory does not exist: {workDir}";
        }

        return null;
    }

    [KernelFunction("run_command")]
    [Description("Execute a shell command in the specified working directory.")]
    public async Task<string> RunCommandAsync(
        [Description("Command to execute")] string command,
        [Description("Working directory for the command")] string workDir)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "Error: Command cannot be empty";
        }

        string? error = ValidateWorkDir(workDir);
        if (error != null)
        {
            return error;
        }

        try
        {
            string shellPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash";
            string shellArgs = OperatingSystem.IsWindows()
                ? $"/c {command}"
                : $"-c \"{command.Replace("\"", "\\\"")}\"";

            Process process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = shellPath,
                    Arguments = shellArgs,
                    WorkingDirectory = Path.GetFullPath(workDir),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();

            using CancellationTokenSource cts = new CancellationTokenSource(DefaultTimeout);

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (TaskCanceledException)
            {
                try
                {
                    process.Kill(true);
                }
                catch
                {
                }

                return $"Error: Command timed out after {DefaultTimeout.TotalSeconds} seconds: {command}";
            }

            string output = await outputTask;
            string errorOutput = await errorTask;

            string result = $"Exit Code: {process.ExitCode}";

            if (!string.IsNullOrWhiteSpace(output))
            {
                if (output.Length > 50000)
                {
                    output = output.Substring(0, 50000) + "\n[Output truncated]";
                }

                result += $"\nOutput:\n{output}";
            }

            if (!string.IsNullOrWhiteSpace(errorOutput))
            {
                if (errorOutput.Length > 10000)
                {
                    errorOutput = errorOutput.Substring(0, 10000) + "\n[Error output truncated]";
                }

                result += $"\nStderr:\n{errorOutput}";
            }

            return result;
        }
        catch (Exception ex)
        {
            return $"Error: Failed to execute command: {ex.Message}";
        }
    }
}
