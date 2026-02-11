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
			ShellTools shellTools = new ShellTools(tempDir);

			TestResolvePath(ctx, shellTools, tempDir);
			TestEdgeCases(ctx, shellTools);
			TestRunCommand(ctx, shellTools);
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void TestResolvePath(TestContext ctx, ShellTools shellTools, string tempDir)
	{
		Type[] types = [typeof(string)];

		// Relative path resolves under workDir.
		string relative = (string)Reflect.Instance(shellTools, "ResolvePath", types, ["subdir/file.txt"])!;
		ctx.Assert(relative.StartsWith(tempDir), "ShellResolvePath: relative resolves under workDir");

		// Absolute path stays absolute.
		string absPath = Path.Combine(Path.GetTempPath(), "absolute.txt");
		string resolved = (string)Reflect.Instance(shellTools, "ResolvePath", types, [absPath])!;
		ctx.AssertEqual(Path.GetFullPath(absPath), resolved, "ShellResolvePath: absolute path unchanged");
	}

	private static void TestEdgeCases(TestContext ctx, ShellTools shellTools)
	{
		// Empty command.
		ToolResult emptyCmd = shellTools.RunCommandAsync("", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(emptyCmd.Response.Contains("Error") && emptyCmd.Response.Contains("empty"), "ShellTools: empty command returns error");

		// Non-existent working directory.
		ToolResult badDir = shellTools.RunCommandAsync("echo test", "/nonexistent/path/that/does/not/exist", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(badDir.Response.Contains("Error"), "ShellTools: non-existent workDir returns error");
	}

	private static void TestRunCommand(TestContext ctx, ShellTools shellTools)
	{
		// Simple echo — may or may not work depending on WSL/bash availability.
		ToolResult echoResult = shellTools.RunCommandAsync("echo hello", "", CancellationToken.None).GetAwaiter().GetResult();
		bool validResponse = echoResult.Response.Contains("Exit Code:") || echoResult.Response.Contains("Error:");
		ctx.Assert(validResponse, "ShellTools: echo returns valid response format");

		// If the command succeeded, verify output was captured.
		if (echoResult.Response.Contains("Exit Code: 0"))
		{
			ctx.Assert(echoResult.Response.Contains("hello"), "ShellTools: echo output captured");
		}
	}
}
