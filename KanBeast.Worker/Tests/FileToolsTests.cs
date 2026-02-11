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
		// Write a multi-line file (10 lines, indices 0-9).
		string multiLine = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
		fileTools.WriteFileAsync("multiline.txt", multiLine, CancellationToken.None).GetAwaiter().GetResult();

		// offset=3, lines=4 → start at index 3 (display line 4), read 4 lines → display lines 4-7.
		ToolResult windowed = fileTools.ReadFileAsync("multiline.txt", "3", "4", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(windowed.Response.Contains("4: line4"), "FileTools: windowed read starts at correct offset");
		ctx.Assert(windowed.Response.Contains("7: line7"), "FileTools: windowed read ends at correct line");
		ctx.Assert(!windowed.Response.Contains("3: line3"), "FileTools: windowed read excludes line before offset");
		ctx.Assert(windowed.Response.Contains("Lines 4-7 of 10"), "FileTools: windowed read has correct header");

		// Blank offset defaults to 0, lines=3 → first 3 lines.
		ToolResult blankOffset = fileTools.ReadFileAsync("multiline.txt", "", "3", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(blankOffset.Response.Contains("1: line1"), "FileTools: blank offset defaults to start");
		ctx.Assert(blankOffset.Response.Contains("Lines 1-3 of 10"), "FileTools: blank offset header correct");

		// offset=7, blank lines → read all remaining from index 7 (display lines 8-10).
		ToolResult blankLines = fileTools.ReadFileAsync("multiline.txt", "7", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(blankLines.Response.Contains("8: line8"), "FileTools: blank lines reads to end");
		ctx.Assert(blankLines.Response.Contains("10: line10"), "FileTools: blank lines includes last line");
		ctx.Assert(blankLines.Response.Contains("Lines 8-10 of 10"), "FileTools: blank lines header correct");

		// Both blank → returns raw content (no line numbers).
		ToolResult rawContent = fileTools.ReadFileAsync("multiline.txt", "", "", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(!rawContent.Response.Contains("Lines"), "FileTools: both blank returns raw content");

		// offset=0, lines=0 explicitly → same as both blank, returns raw content.
		ToolResult zeros = fileTools.ReadFileAsync("multiline.txt", "0", "0", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(!zeros.Response.Contains("Lines"), "FileTools: explicit zeros returns raw content");

		// Invalid offset value.
		ToolResult badOffset = fileTools.ReadFileAsync("multiline.txt", "abc", "3", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(badOffset.Response.Contains("Error"), "FileTools: invalid offset returns error");

		// Negative offset.
		ToolResult negOffset = fileTools.ReadFileAsync("multiline.txt", "-1", "3", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(negOffset.Response.Contains("Error"), "FileTools: negative offset returns error");

		// Offset beyond file length clamps to last line.
		ToolResult beyondOffset = fileTools.ReadFileAsync("multiline.txt", "99", "5", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(beyondOffset.Response.Contains("10: line10"), "FileTools: offset beyond length clamps to last line");

		// Lines exceeding remainder clamps to end.
		ToolResult beyondLines = fileTools.ReadFileAsync("multiline.txt", "8", "50", CancellationToken.None).GetAwaiter().GetResult();
		ctx.Assert(beyondLines.Response.Contains("9: line9"), "FileTools: lines beyond remainder clamps to end");
		ctx.Assert(beyondLines.Response.Contains("10: line10"), "FileTools: clamped read includes last line");
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
