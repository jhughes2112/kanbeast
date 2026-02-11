using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class FileToolsTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  FileToolsTests");

		string tempDir = Path.Combine(Path.GetTempPath(), $"kanbeast_test_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
		{
			FileTools fileTools = new FileTools(tempDir);

			TestResolvePath(ctx, fileTools, tempDir);
			TestWriteAndRead(ctx, fileTools);
			TestReadWithOffsetLines(ctx, fileTools);
			TestEditFile(ctx, fileTools);
			TestEdgeErrors(ctx, fileTools);
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void TestResolvePath(TestContext ctx, FileTools fileTools, string tempDir)
	{
		Type[] types = [typeof(string)];

		// Relative path resolves under workDir.
		string relative = (string)Reflect.Instance(fileTools, "ResolvePath", types, ["subdir/file.txt"])!;
		ctx.Assert(relative.StartsWith(tempDir), "ResolvePath: relative resolves under workDir");
		ctx.Assert(relative.Contains("subdir"), "ResolvePath: relative preserves subdirectory");

		// Absolute path stays absolute.
		string absPath = Path.Combine(Path.GetTempPath(), "absolute.txt");
		string resolved = (string)Reflect.Instance(fileTools, "ResolvePath", types, [absPath])!;
		ctx.AssertEqual(Path.GetFullPath(absPath), resolved, "ResolvePath: absolute path unchanged");
	}

	private static void TestWriteAndRead(TestContext ctx, FileTools fileTools)
	{
		// Write a file.
		ToolResult writeResult = fileTools.WriteFileAsync("test.txt", "hello world", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(writeResult.Response.Contains("File written"), "FileTools: write succeeds");

		// Read it back.
		ToolResult readResult = fileTools.ReadFileAsync("test.txt", "", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.AssertEqual("hello world", readResult.Response, "FileTools: read returns written content");
	}

	private static void TestReadWithOffsetLines(TestContext ctx, FileTools fileTools)
	{
		// Write a multi-line file.
		string multiLine = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
		fileTools.WriteFileAsync("multiline.txt", multiLine, CancellationToken.None).GetAwaiter().GetResult();

		// Read with offset=5, lines=3.
		ToolResult windowed = fileTools.ReadFileAsync("multiline.txt", "5", "3", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(windowed.Response.Contains("line5"), "FileTools: windowed read includes center line");
		ctx.Assert(windowed.Response.Contains("Lines"), "FileTools: windowed read has line range header");

		// Invalid offset value.
		ToolResult badOffset = fileTools.ReadFileAsync("multiline.txt", "abc", "3", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(badOffset.Response.Contains("Error"), "FileTools: invalid offset returns error");

		// Zero offset.
		ToolResult zeroOffset = fileTools.ReadFileAsync("multiline.txt", "0", "3", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(zeroOffset.Response.Contains("Error"), "FileTools: zero offset returns error");

		// Zero lines.
		ToolResult zeroLines = fileTools.ReadFileAsync("multiline.txt", "5", "0", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(zeroLines.Response.Contains("Error"), "FileTools: zero lines returns error");
	}

	private static void TestEditFile(TestContext ctx, FileTools fileTools)
	{
		// Write initial content.
		fileTools.WriteFileAsync("editable.txt", "alpha beta gamma", CancellationToken.None).GetAwaiter().GetResult();

		// Replace single occurrence.
		ToolResult editResult = fileTools.EditFileAsync("editable.txt", "beta", "delta", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(editResult.Response.Contains("File edited"), "FileTools: edit succeeds");

		// Verify content changed.
		ToolResult verifyResult = fileTools.ReadFileAsync("editable.txt", "", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.AssertEqual("alpha delta gamma", verifyResult.Response, "FileTools: edit applied correctly");

		// Non-existent content.
		ToolResult notFound = fileTools.EditFileAsync("editable.txt", "zzzzz", "yyy", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(notFound.Response.Contains("Error") && notFound.Response.Contains("not found"), "FileTools: edit non-existent content returns error");

		// Duplicate content matched multiple times.
		fileTools.WriteFileAsync("dupe.txt", "aaa bbb aaa", CancellationToken.None).GetAwaiter().GetResult();
		ToolResult dupeResult = fileTools.EditFileAsync("dupe.txt", "aaa", "ccc", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(dupeResult.Response.Contains("Error") && dupeResult.Response.Contains("multiple"), "FileTools: edit duplicate content returns error");
	}

	private static void TestEdgeErrors(TestContext ctx, FileTools fileTools)
	{
		// Empty path on write.
		ToolResult emptyPath = fileTools.WriteFileAsync("", "content", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(emptyPath.Response.Contains("Error"), "FileTools: write empty path returns error");

		// Read non-existent file.
		ToolResult noFile = fileTools.ReadFileAsync("nonexistent.txt", "", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(noFile.Response.Contains("Error") && noFile.Response.Contains("not found"), "FileTools: read non-existent returns error");

		// Edit with empty oldContent.
		fileTools.WriteFileAsync("edit_edge.txt", "content", CancellationToken.None).GetAwaiter().GetResult();
		ToolResult emptyOld = fileTools.EditFileAsync("edit_edge.txt", "", "new", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(emptyOld.Response.Contains("Error"), "FileTools: edit empty oldContent returns error");

		// Edit non-existent file.
		ToolResult editMissing = fileTools.EditFileAsync("ghost.txt", "old", "new", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(editMissing.Response.Contains("Error") && editMissing.Response.Contains("not found"), "FileTools: edit non-existent file returns error");

		// Write creates subdirectory automatically.
		ToolResult subdir = fileTools.WriteFileAsync("newdir/nested.txt", "hi", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(subdir.Response.Contains("File written"), "FileTools: write creates subdirectory");

		// Empty read path.
		ToolResult emptyReadPath = fileTools.ReadFileAsync("", "", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(emptyReadPath.Response.Contains("Error"), "FileTools: read empty path returns error");
	}
}
