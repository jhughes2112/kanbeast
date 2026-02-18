using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to execute shell commands.
// Default CWD is the git repository folder. Uses bash (via WSL on Windows).
public static class ShellTools
{
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

	[Description("""
		Execute a bash command, returns stdout, stderr, and exit code. Times out after 60s.
		Do NOT use for file operations (use read_file, write_file, edit_file, list_directory) or search (use glob, grep).
		Reserve for: builds, tests, git, package management, system utilities.
		Use absolute paths. Prefer start_shell for stateful use or long running processes time out.
		""")]
	public static async Task<ToolResult> RunCommandAsync(
		[Description("The bash command to execute. Use && to chain dependent commands (stops on failure). Use ; to chain independent commands. Do not use newlines; keep the command on one line.")] string command,
		[Description("Absolute path to the working directory for this command. Pass empty string to use the repository root.")] string workDir,
		[Description("Optional regular expression to filter the output lines. Only lines matching this regex will be included in the result.")] string matchRegex,
		ToolContext context)
	{
		ToolResult result;
		CancellationToken cancellationToken = WorkerSession.CancellationToken;

		if (string.IsNullOrWhiteSpace(command))
		{
			result = new ToolResult("Error: Command cannot be empty", false, false);
		}
		else if (!string.IsNullOrWhiteSpace(workDir) && !Path.IsPathRooted(workDir))
		{
			result = new ToolResult($"Error: Working directory must be an absolute path: {workDir}", false, false);
		}
		else
		{
			string effectiveWorkDir = string.IsNullOrWhiteSpace(workDir) ? WorkerSession.WorkDir : Path.GetFullPath(workDir);

			if (!Directory.Exists(effectiveWorkDir))
			{
				result = new ToolResult($"Error: Working directory does not exist: {workDir}", false, false);
			}
			else
			{
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

					using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
					cts.CancelAfter(DefaultTimeout);

					Task<string> outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
					Task<string> errorTask = process.StandardError.ReadToEndAsync(cts.Token);

					try
					{
						await process.WaitForExitAsync(cts.Token);
					}
					catch (OperationCanceledException)
					{
						try
						{
							process.Kill(true);
						}
						catch
						{
						}

						result = new ToolResult($"Error: Command timed out or cancelled after {DefaultTimeout.TotalSeconds} seconds: {command}", false, false);
						return result;
					}

					string output = await outputTask;
					string errorOutput = await errorTask;

					// Apply regex filter if provided
					if (!string.IsNullOrEmpty(matchRegex))
					{
						try
						{
							System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(matchRegex);
							output = FilterLines(output, regex);
							errorOutput = FilterLines(errorOutput, regex);
						}
						catch (ArgumentException ex)
						{
							result = new ToolResult($"Error: Invalid regex pattern: {ex.Message}", false, false);
							return result;
						}
					}

					StringBuilder responseBuilder = new StringBuilder();

					if (!string.IsNullOrWhiteSpace(output))
					{
						if (output.Length > 50000)
						{
							output = output.Substring(0, 50000) + "\n[Output truncated]";
						}

						responseBuilder.Append(output);
						if (!output.EndsWith('\n'))
						{
							responseBuilder.AppendLine();
						}
					}

					if (!string.IsNullOrWhiteSpace(errorOutput))
					{
						if (errorOutput.Length > 10000)
						{
							errorOutput = errorOutput.Substring(0, 10000) + "\n[Error output truncated]";
						}

						if (responseBuilder.Length > 0)
						{
							responseBuilder.AppendLine();
						}
						responseBuilder.AppendLine("Stderr:");
						responseBuilder.Append(errorOutput);
						if (!errorOutput.EndsWith('\n'))
						{
							responseBuilder.AppendLine();
						}
					}

					if (responseBuilder.Length > 0)
					{
						responseBuilder.AppendLine();
					}
					responseBuilder.Append($"Exit Code: {process.ExitCode}");

					result = new ToolResult(responseBuilder.ToString(), false, false);
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: Failed to execute command: {ex.Message}", false, false);
				}
			}
		}

		return result;
	}

	private static string FilterLines(string text, System.Text.RegularExpressions.Regex regex)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text;
		}

		string[] lines = text.Split('\n');
		StringBuilder result = new StringBuilder();

		foreach (string line in lines)
		{
			if (regex.IsMatch(line))
			{
				result.Append(line);
				result.Append('\n');
			}
		}

		return result.ToString();
	}
}
