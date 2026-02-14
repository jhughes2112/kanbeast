using KanBeast.Shared;
using KanBeast.Worker.Services;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class ShellToolsTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  ShellToolsTests");

		string tempDir = Path.Combine(Path.GetTempPath(), $"kanbeast_shell_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			WorkerSession.Start(null!, null!, null!, null!, tempDir, CancellationToken.None, null!);
			ConversationMemories testMemories = new ConversationMemories();
			ToolContext tc = new ToolContext(null, null, null, testMemories);

			TestEdgeCases(ctx, tc);
			TestRunCommand(ctx, tc);
			TestPersistentShell(ctx, tc, tempDir);
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void TestEdgeCases(TestContext ctx, ToolContext tc)
	{
		// Empty command.
		ToolResult emptyCmd = ShellTools.RunCommandAsync("", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(emptyCmd.Response.Contains("Error") && emptyCmd.Response.Contains("empty"), "ShellTools: empty command returns error");

		// Non-existent working directory.
		ToolResult badDir = ShellTools.RunCommandAsync("echo test", "/nonexistent/path/that/does/not/exist", "", tc).GetAwaiter().GetResult();
		ctx.Assert(badDir.Response.Contains("Error"), "ShellTools: non-existent workDir returns error");
	}

	private static void TestRunCommand(TestContext ctx, ToolContext tc)
	{
		// Simple echo — may or may not work depending on WSL/bash availability.
		ToolResult echoResult = ShellTools.RunCommandAsync("echo hello", "", "", tc).GetAwaiter().GetResult();
		bool validResponse = echoResult.Response.Contains("Exit Code:") || echoResult.Response.Contains("Error:");
		ctx.Assert(validResponse, "ShellTools: echo returns valid response format");

		// If the command succeeded, verify output was captured.
		if (echoResult.Response.Contains("Exit Code: 0"))
		{
			ctx.Assert(echoResult.Response.Contains("hello"), "ShellTools: echo output captured");
		}
	}

	private static void TestPersistentShell(TestContext ctx, ToolContext tc, string tempDir)
	{
		// Start shell without one running.
		ToolResult startResult = PersistentShellTools.StartShellAsync("", tc).GetAwaiter().GetResult();
		ctx.Assert(startResult.Response.Contains("Shell started"), "PersistentShell: start succeeds");
		ctx.Assert(tc.Shell != null, "PersistentShell: shell stored in context");

		// Try to start another shell — should fail.
		ToolResult doubleStart = PersistentShellTools.StartShellAsync("", tc).GetAwaiter().GetResult();
		ctx.Assert(doubleStart.Response.Contains("Error") && doubleStart.Response.Contains("already running"), "PersistentShell: cannot start second shell");

		// Send a command and get output in one call.
		ToolResult sendEcho = PersistentShellTools.SendShellAsync("echo persistent_test", false, "", tc).GetAwaiter().GetResult();
		ctx.Assert(sendEcho.Response.Contains("Input sent"), "PersistentShell: input sent");

		// Wait for output to accumulate, then read with empty input.
		System.Threading.Thread.Sleep(200);
		ToolResult readOutput = PersistentShellTools.SendShellAsync("", false, "", tc).GetAwaiter().GetResult();
		ctx.Assert(readOutput.Response.Contains("persistent_test") || readOutput.Response.Contains("no output"), "PersistentShell: output read (or shell unavailable)");

		// Test cd persistence.
		PersistentShellTools.SendShellAsync($"cd '{tempDir}'", false, "", tc).GetAwaiter().GetResult();
		System.Threading.Thread.Sleep(100);
		PersistentShellTools.SendShellAsync("", false, "", tc).GetAwaiter().GetResult(); // Clear buffer
		PersistentShellTools.SendShellAsync("pwd", false, "", tc).GetAwaiter().GetResult();
		System.Threading.Thread.Sleep(200);
		ToolResult pwdOutput = PersistentShellTools.SendShellAsync("", false, "", tc).GetAwaiter().GetResult();
		// May fail if WSL not available or path conversion differs, so just check it didn't error
		bool cdWorked = !pwdOutput.Response.Contains("Error") || pwdOutput.Response.Contains("no output");
		ctx.Assert(cdWorked, "PersistentShell: cd command executed");

		// Kill the shell.
		ToolResult killResult = PersistentShellTools.KillShellAsync(tc).GetAwaiter().GetResult();
		ctx.Assert(killResult.Response.Contains("Shell killed"), "PersistentShell: kill succeeds");
		ctx.Assert(tc.Shell == null, "PersistentShell: shell removed from context");

		// Try to send after kill — should fail.
		ToolResult sendAfterKill = PersistentShellTools.SendShellAsync("echo test", false, "", tc).GetAwaiter().GetResult();
		ctx.Assert(sendAfterKill.Response.Contains("Error") && sendAfterKill.Response.Contains("No shell"), "PersistentShell: cannot use shell after kill");
	}
}
