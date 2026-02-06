using System.ComponentModel;
using System.Diagnostics;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to execute shell commands.
// Default CWD is the git repository folder. Uses bash (via WSL on Windows).
public class ShellTools : IToolProvider
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private readonly string _workDir;
    private readonly Dictionary<LlmRole, List<Tool>> _toolsByRole;

    public ShellTools(string workDir)
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
        List<Tool> sharedTools = new List<Tool>();
        ToolHelper.AddTools(sharedTools, this, nameof(RunCommandAsync));

        Dictionary<LlmRole, List<Tool>> result = new Dictionary<LlmRole, List<Tool>>
        {
            [LlmRole.ManagerPlanning] = sharedTools,
            [LlmRole.ManagerImplementing] = sharedTools,
            [LlmRole.Developer] = sharedTools,
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

	[Description("Execute a shell command.")]
	public async Task<string> RunCommandAsync(
		[Description("Command to execute")] string command,
		[Description("Working directory (or empty for repository root)")] string workDir)
	{
		if (string.IsNullOrWhiteSpace(command))
		{
			return "Error: Command cannot be empty";
		}

		string effectiveWorkDir = string.IsNullOrWhiteSpace(workDir) ? _workDir : ResolvePath(workDir);

		if (!Directory.Exists(effectiveWorkDir))
		{
			return $"Error: Working directory does not exist: {workDir}";
		}

		try
		{
			string shellPath;
			string shellArgs;

			if (OperatingSystem.IsWindows())
			{
				string wslPath = effectiveWorkDir.Replace("\\", "/");
				if (wslPath.Length >= 2 && wslPath[1] == ':')
				{
					wslPath = $"/mnt/{char.ToLower(wslPath[0])}{wslPath.Substring(2)}";
				}

				shellPath = "wsl";
				shellArgs = $"bash -c \"cd '{wslPath}' && {command.Replace("\"", "\\\"")}\"";
			}
			else
			{
				shellPath = "/bin/bash";
				shellArgs = $"-c \"{command.Replace("\"", "\\\"")}\"";
			}

			Process process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = shellPath,
					Arguments = shellArgs,
					WorkingDirectory = OperatingSystem.IsWindows() ? null : effectiveWorkDir,
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
