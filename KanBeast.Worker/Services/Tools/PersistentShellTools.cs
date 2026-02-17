using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace KanBeast.Worker.Services.Tools;

// Holds the runtime state of a persistent bash shell session.
public class ShellState
{
	public Process? Process { get; set; }
	public StreamWriter? Stdin { get; set; }
	public Task? OutputReaderTask { get; set; }
	public Task? ErrorReaderTask { get; set; }
	public StringBuilder StdoutBuffer { get; } = new StringBuilder();
	public StringBuilder StderrBuffer { get; } = new StringBuilder();
	public object BufferLock { get; } = new object();
	public bool IsRunning => Process?.HasExited == false;
}

// Tools for persistent interactive bash shell.
// Output accumulates in buffers; input and output happen through separate tool calls.
public static class PersistentShellTools
{
	[Description("""
		Start a persistent background bash shell. Only one shell may be running at a time.
		The shell starts in the repository root. To interrupt a hung or long-running process, send the two characters ^C

		Only use the persistent shell instead of run_command when stateful manipulation of a bash shell is necessary!
		- When cd, environment variables, or virtual-env activation must persist across commands.
		- When running long-lived or streaming processes (dev servers, watch-mode tests, tailing logs) that would be terminated by the 60 second timer of run_command.
		- When interactive programs require input mid-execution.

		Prefer run_command when work can be done with a single, self-contained command whose output can be obtained immediately.
		""")]
 
	public static async Task<ToolResult> StartShellAsync(
		[Description("Working directory for the shell. Pass empty string to use repository root.")] string workDir,
		ToolContext context)
	{
		ToolResult result;

		if (context.Shell != null)
		{
			result = new ToolResult("Error: Shell already running. Use kill_shell first to start a new one.", false);
		}
		else
		{
			try
			{
				string effectiveWorkDir = string.IsNullOrWhiteSpace(workDir) ? WorkerSession.WorkDir : Path.GetFullPath(workDir);

				if (!Directory.Exists(effectiveWorkDir))
				{
					result = new ToolResult($"Error: Working directory does not exist: {workDir}", false);
				}
				else
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
						shellArgs = $"bash --noprofile --norc -c \"cd '{wslPath}' && exec bash --noprofile --norc\"";
					}
					else
					{
						shellPath = "/bin/bash";
						shellArgs = "--noprofile --norc";
					}

					ProcessStartInfo psi = new ProcessStartInfo
					{
						FileName = shellPath,
						Arguments = shellArgs,
						WorkingDirectory = OperatingSystem.IsWindows() ? null : effectiveWorkDir,
						RedirectStandardInput = true,
						RedirectStandardOutput = true,
						RedirectStandardError = true,
						UseShellExecute = false,
						CreateNoWindow = true
					};

					Process? process = Process.Start(psi);
					if (process == null)
					{
						throw new Exception("Failed to start bash process");
					}

					CancellationToken cancellationToken = WorkerSession.CancellationToken;

					ShellState state = new ShellState
					{
						Process = process,
						Stdin = process.StandardInput
					};

					state.OutputReaderTask = Task.Run(() => ReadStreamAsync(process.StandardOutput, state.StdoutBuffer, state.BufferLock, cancellationToken), cancellationToken);
					state.ErrorReaderTask = Task.Run(() => ReadStreamAsync(process.StandardError, state.StderrBuffer, state.BufferLock, cancellationToken), cancellationToken);

					context.Shell = state;

					await Task.Delay(100, cancellationToken);
					await WriteInputAsync("unset HISTFILE; PS1=''", context);
					await Task.Delay(50, cancellationToken);
					DrainBuffers(context);

					result = new ToolResult($"Shell started in: {effectiveWorkDir}\nUse send_shell_input to send commands and read_shell_output to see results.", false);
				}
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Failed to start shell: {ex.Message}", false);
			}
		}

		return result;
	}

	[Description("""
		Interact with the persistent bash shell. Optionally sends input, then returns all accumulated stdout/stderr since the last call.
		- To just read output: pass empty input.
		- To send a command unconditionally (e.g. ^C to interrupt): pass the input with onlyIfNoOutput=false.
		- To send a command only when the shell is apparently idle: pass the input with onlyIfNoOutput=true. If any output is pending, the input is withheld and only output is returned.

		Do NOT use for file operations — use read_file, write_file, edit_file instead of cat, head, tail, awk, sed.
		Do NOT use for search — use glob and grep tools instead of find, grep, rg.
		Do NOT use with any programs that require a proper raw mode TTY, isatty() will fail.

		Best practices:
		- Always quote file paths containing spaces with double quotes.
		- Use absolute paths to avoid working-directory confusion.
		- Before creating directories or files, verify the parent exists with list_directory.
		""")]
	public static async Task<ToolResult> SendShellAsync(
		[Description("Input to send. Use '^C' to send Ctrl+C interrupt. Pass empty string to just read output without sending anything.")] string input,
		[Description("When true, input is only sent if no output has accumulated. Use this to avoid sending commands while prior output is still arriving. When false, input is sent unconditionally.")] bool onlyIfNoOutput,
		[Description("Optional regular expression to filter the output lines. Only lines matching this regex will be included in the result. Any lines that do not match will no longer be available to read.")] string matchRegex,
		ToolContext context)
	{
		ToolResult result;

		if (context.Shell == null)
		{
			result = new ToolResult("Error: No shell running. Use start_shell first.", false);
		}
		else
		{
			try
			{
				bool sent = false;

				if (!string.IsNullOrEmpty(input))
				{
					if (!context.Shell.IsRunning)
					{
						result = new ToolResult("Error: Shell has exited.", false);
						return result;
					}

					bool hasPending;
					lock (context.Shell.BufferLock)
					{
						hasPending = context.Shell.StdoutBuffer.Length > 0 || context.Shell.StderrBuffer.Length > 0;
					}

					if (!onlyIfNoOutput || !hasPending)
					{
						await WriteInputAsync(input, context);
						sent = true;
					}
				}

				(string stdout, string stderr) = DrainBuffers(context);

				// Apply regex filter if provided
				if (!string.IsNullOrEmpty(matchRegex))
				{
					try
					{
						System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(matchRegex);
						stdout = FilterLines(stdout, regex);
						stderr = FilterLines(stderr, regex);
					}
					catch (ArgumentException ex)
					{
						result = new ToolResult($"Error: Invalid regex pattern: {ex.Message}", false);
						return result;
					}
				}

				StringBuilder sb = new StringBuilder();

				if (sent)
				{
					sb.AppendLine("[Input sent]");
				}
				else if (!string.IsNullOrEmpty(input))
				{
					sb.AppendLine("[Input withheld — output was pending]");
				}

				if (!string.IsNullOrEmpty(stdout))
				{
					if (stdout.Length > 50000)
					{
						stdout = stdout.Substring(0, 50000) + "\n[Output truncated at 50000 characters]";
					}
					sb.AppendLine("Stdout:");
					sb.Append(stdout);
					if (!stdout.EndsWith('\n'))
					{
						sb.AppendLine();
					}
				}

				if (!string.IsNullOrEmpty(stderr))
				{
					if (stderr.Length > 10000)
					{
						stderr = stderr.Substring(0, 10000) + "\n[Error output truncated]";
					}
					sb.AppendLine("Stderr:");
					sb.Append(stderr);
					if (!stderr.EndsWith('\n'))
					{
						sb.AppendLine();
					}
				}

				if (sb.Length == 0)
				{
					sb.AppendLine("(no output)");
				}

				result = new ToolResult(sb.ToString(), false);
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: {ex.Message}", false);
			}
		}

		return result;
	}

	[Description("Kill the persistent shell and clean up resources. Use this when you're done with the shell or if it becomes unresponsive.")]
	public static Task<ToolResult> KillShellAsync(ToolContext context)
	{
		ToolResult result;

		if (context.Shell == null)
		{
			result = new ToolResult("Error: No shell running.", false);
		}
		else
		{
			try
			{
				context.DestroyShell();
				result = new ToolResult("Shell killed.", false);
			}
			catch (Exception ex)
			{
				result = new ToolResult($"Error: Failed to kill shell: {ex.Message}", false);
			}
		}

		return Task.FromResult(result);
	}

	// Called by ToolContext.DestroyShell() to dispose shell resources.
	internal static void Destroy(ShellState state)
	{
		try
		{
			state.Stdin?.Dispose();
			state.Process?.Kill(true);
			state.Process?.Dispose();
		}
		catch
		{
			// Best effort cleanup
		}
	}

	private static async Task WriteInputAsync(string input, ToolContext context)
	{
		ShellState state = context.Shell!;

		if (input == "^C")
		{
			await state.Stdin!.WriteAsync('\x03');
		}
		else
		{
			await state.Stdin!.WriteLineAsync(input);
		}

		await state.Stdin.FlushAsync(WorkerSession.CancellationToken);
	}

	private static (string stdout, string stderr) DrainBuffers(ToolContext context)
	{
		ShellState state = context.Shell!;

		lock (state.BufferLock)
		{
			string stdout = state.StdoutBuffer.ToString();
			string stderr = state.StderrBuffer.ToString();
			state.StdoutBuffer.Clear();
			state.StderrBuffer.Clear();
			return (stdout, stderr);
		}
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

	private static async Task ReadStreamAsync(StreamReader reader, StringBuilder buffer, object bufferLock, CancellationToken cancellationToken)
	{
		char[] buf = new char[4096];
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				int read = await reader.ReadAsync(buf, 0, buf.Length);
				if (read == 0)
				{
					break;
				}

				lock (bufferLock)
				{
					buffer.Append(buf, 0, read);
				}
			}
		}
		catch
		{
			// Reader died or cancelled
		}
	}
}
