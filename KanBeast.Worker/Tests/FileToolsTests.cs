using System.Text.Json.Nodes;
using KanBeast.Worker.Services;
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
			WorkerSession.Start(null!, null!, null!, null!, tempDir, CancellationToken.None, null);
			LlmMemories testMemories = new LlmMemories();
			ToolContext tc = new ToolContext(null, null, null, testMemories);

			TestWriteAndRead(ctx, tc, tempDir);
			TestCatNFormat(ctx, tc, tempDir);
			TestReadWithOffsetLines(ctx, tc, tempDir);
			TestEditFile(ctx, tc, tempDir);
			TestMultiEditFile(ctx, tc, tempDir);
			TestReadFirstGuard(ctx, tc, tempDir);
			TestEdgeErrors(ctx, tc, tempDir);
		}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void TestWriteAndRead(TestContext ctx, ToolContext tc, string tempDir)
	{
		string testFile = Path.Combine(tempDir, "test.txt");

		// Write a new file (no prior read needed for new files).
		ToolResult writeResult = FileTools.WriteFileAsync(testFile, "hello world", tc).GetAwaiter().GetResult();
		ctx.Assert(writeResult.Response.Contains("File written"), "FileTools: write succeeds");

		// Read it back — always cat -n format.
		ToolResult readResult = FileTools.ReadFileAsync(testFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(readResult.Response.Contains("1\thello world"), "FileTools: read returns cat -n numbered content");

		// Relative path is rejected.
		ToolResult relativePath = FileTools.ReadFileAsync("test.txt", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(relativePath.Response.Contains("Error") && relativePath.Response.Contains("absolute"), "FileTools: relative path returns error");
	}

	private static void TestCatNFormat(TestContext ctx, ToolContext tc, string tempDir)
	{
		string catFile = Path.Combine(tempDir, "catn.txt");

		// Write a file with blank lines to verify they are preserved.
		FileTools.WriteFileAsync(catFile, "first\n\nthird\nfourth", tc).GetAwaiter().GetResult();

		ToolResult readResult = FileTools.ReadFileAsync(catFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(readResult.Response.Contains("1\tfirst"), "CatN: line 1 present");
		ctx.Assert(readResult.Response.Contains("2\t"), "CatN: blank line 2 preserved");
		ctx.Assert(readResult.Response.Contains("3\tthird"), "CatN: line 3 present");
		ctx.Assert(readResult.Response.Contains("4\tfourth"), "CatN: line 4 present");
		ctx.Assert(!readResult.Response.Contains("Showing"), "CatN: full file has no header");
	}

	private static void TestReadWithOffsetLines(TestContext ctx, ToolContext tc, string tempDir)
	{
		string multiFile = Path.Combine(tempDir, "multiline.txt");

		// Write a multi-line file (10 lines).
		string multiLine = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10";
		FileTools.WriteFileAsync(multiFile, multiLine, tc).GetAwaiter().GetResult();

		// offset=3, lines=4 → 1-based: start at line 3, read 4 lines → lines 3-6.
		ToolResult windowed = FileTools.ReadFileAsync(multiFile, "3", "4", tc).GetAwaiter().GetResult();
		ctx.Assert(windowed.Response.Contains("3\tline3"), "FileTools: windowed read starts at correct offset");
		ctx.Assert(windowed.Response.Contains("6\tline6"), "FileTools: windowed read ends at correct line");
		ctx.Assert(!windowed.Response.Contains("2\tline2"), "FileTools: windowed read excludes line before offset");
		ctx.Assert(!windowed.Response.Contains("7\tline7"), "FileTools: windowed read excludes line after range");
		ctx.Assert(windowed.Response.Contains("Showing lines 3-6"), "FileTools: windowed read has correct header");

		// Blank offset defaults to 1, lines=3 → lines 1-3.
		ToolResult blankOffset = FileTools.ReadFileAsync(multiFile, "", "3", tc).GetAwaiter().GetResult();
		ctx.Assert(blankOffset.Response.Contains("1\tline1"), "FileTools: blank offset defaults to start");
		ctx.Assert(blankOffset.Response.Contains("Showing lines 1-3"), "FileTools: blank offset header correct");

		// offset=7, blank lines → read up to MaxLines from line 7 → lines 7-10.
		ToolResult blankLines = FileTools.ReadFileAsync(multiFile, "7", "", tc).GetAwaiter().GetResult();
		ctx.Assert(blankLines.Response.Contains("7\tline7"), "FileTools: 1-based offset reads correct line");
		ctx.Assert(blankLines.Response.Contains("10\tline10"), "FileTools: blank lines includes last line");
		ctx.Assert(blankLines.Response.Contains("Showing lines 7-10"), "FileTools: blank lines header correct");

		// Both blank → up to 10,000 lines, no header.
		ToolResult fullFile = FileTools.ReadFileAsync(multiFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(fullFile.Response.Contains("1\tline1"), "FileTools: full read starts at line 1");
		ctx.Assert(fullFile.Response.Contains("10\tline10"), "FileTools: full read includes last line");
		ctx.Assert(!fullFile.Response.Contains("Showing"), "FileTools: full read has no header");

		// offset=0, lines=0 → same as both blank (0 treated as 1).
		ToolResult zeros = FileTools.ReadFileAsync(multiFile, "0", "0", tc).GetAwaiter().GetResult();
		ctx.Assert(zeros.Response.Contains("1\tline1"), "FileTools: explicit zeros returns full file");
		ctx.Assert(!zeros.Response.Contains("Showing"), "FileTools: explicit zeros has no header");

		// Invalid offset value.
		ToolResult badOffset = FileTools.ReadFileAsync(multiFile, "abc", "3", tc).GetAwaiter().GetResult();
		ctx.Assert(badOffset.Response.Contains("Error"), "FileTools: invalid offset returns error");

		// Negative offset.
		ToolResult negOffset = FileTools.ReadFileAsync(multiFile, "-1", "3", tc).GetAwaiter().GetResult();
		ctx.Assert(negOffset.Response.Contains("Error"), "FileTools: negative offset returns error");

		// Offset beyond file length.
		ToolResult beyondOffset = FileTools.ReadFileAsync(multiFile, "99", "5", tc).GetAwaiter().GetResult();
		ctx.Assert(beyondOffset.Response.Contains("beyond the end"), "FileTools: offset beyond length returns informative error");

		// Lines exceeding file length still returns available lines.
		ToolResult beyondLines = FileTools.ReadFileAsync(multiFile, "8", "50", tc).GetAwaiter().GetResult();
		ctx.Assert(beyondLines.Response.Contains("8\tline8"), "FileTools: lines beyond file starts correct");
		ctx.Assert(beyondLines.Response.Contains("10\tline10"), "FileTools: lines beyond file includes last line");
	}

	private static void TestEditFile(TestContext ctx, ToolContext tc, string tempDir)
	{
		string editFile = Path.Combine(tempDir, "editable.txt");

		// Write initial content (new file — tracked by write).
		FileTools.WriteFileAsync(editFile, "alpha beta gamma", tc).GetAwaiter().GetResult();

		// Replace single occurrence (write tracked the file, so edit is allowed).
		ToolResult editResult = FileTools.EditFileAsync(editFile, "beta", "delta", false, tc).GetAwaiter().GetResult();
		ctx.Assert(editResult.Response.Contains("File edited"), "FileTools: edit succeeds");

		// Verify content changed.
		ToolResult verifyResult = FileTools.ReadFileAsync(editFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(verifyResult.Response.Contains("alpha delta gamma"), "FileTools: edit applied correctly");

		// Non-existent content.
		ToolResult notFound = FileTools.EditFileAsync(editFile, "zzzzz", "yyy", false, tc).GetAwaiter().GetResult();
		ctx.Assert(notFound.Response.Contains("Error") && notFound.Response.Contains("not found"), "FileTools: edit non-existent content returns error");

		// Duplicate content matched multiple times.
		string dupeFile = Path.Combine(tempDir, "dupe.txt");
		FileTools.WriteFileAsync(dupeFile, "aaa bbb aaa", tc).GetAwaiter().GetResult();
		ToolResult dupeResult = FileTools.EditFileAsync(dupeFile, "aaa", "ccc", false, tc).GetAwaiter().GetResult();
		ctx.Assert(dupeResult.Response.Contains("Error") && dupeResult.Response.Contains("multiple"), "FileTools: edit duplicate content returns error");
	}

	private static void TestMultiEditFile(TestContext ctx, ToolContext tc, string tempDir)
	{
		// Write initial content.
		string multiEditFile = Path.Combine(tempDir, "multi_edit.txt");
		FileTools.WriteFileAsync(multiEditFile, "alpha beta gamma delta", tc).GetAwaiter().GetResult();

		// Apply two edits in sequence.
		JsonArray edits = new JsonArray
		{
			new JsonObject { ["oldContent"] = "alpha", ["newContent"] = "AAA" },
			new JsonObject { ["oldContent"] = "gamma", ["newContent"] = "GGG" }
		};
		ToolResult multiResult = FileTools.MultiEditFileAsync(multiEditFile, edits, tc).GetAwaiter().GetResult();
		ctx.Assert(multiResult.Response.Contains("2 edit(s) applied"), "MultiEdit: two edits applied");

		// Verify both edits took effect.
		ToolResult verify = FileTools.ReadFileAsync(multiEditFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(verify.Response.Contains("AAA beta GGG delta"), "MultiEdit: both replacements applied correctly");

		// Second edit operates on result of first edit.
		string chainFile = Path.Combine(tempDir, "chain_edit.txt");
		FileTools.WriteFileAsync(chainFile, "foo bar", tc).GetAwaiter().GetResult();
		JsonArray chainEdits = new JsonArray
		{
			new JsonObject { ["oldContent"] = "foo", ["newContent"] = "baz" },
			new JsonObject { ["oldContent"] = "baz bar", ["newContent"] = "result" }
		};
		ToolResult chainResult = FileTools.MultiEditFileAsync(chainFile, chainEdits, tc).GetAwaiter().GetResult();
		ctx.Assert(chainResult.Response.Contains("2 edit(s) applied"), "MultiEdit: chained edits applied");
		ToolResult chainVerify = FileTools.ReadFileAsync(chainFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(chainVerify.Response.Contains("result"), "MultiEdit: chained edits produce correct content");

		// Atomic failure - second edit fails, no edits applied.
		string atomicFile = Path.Combine(tempDir, "atomic_edit.txt");
		FileTools.WriteFileAsync(atomicFile, "one two three", tc).GetAwaiter().GetResult();
		JsonArray atomicEdits = new JsonArray
		{
			new JsonObject { ["oldContent"] = "one", ["newContent"] = "1" },
			new JsonObject { ["oldContent"] = "NOTFOUND", ["newContent"] = "X" }
		};
		ToolResult atomicResult = FileTools.MultiEditFileAsync(atomicFile, atomicEdits, tc).GetAwaiter().GetResult();
		ctx.Assert(atomicResult.Response.Contains("Error") && atomicResult.Response.Contains("Edit 2"), "MultiEdit: failure reports which edit failed");
		ctx.Assert(atomicResult.Response.Contains("No edits were applied"), "MultiEdit: failure is atomic");
		ToolResult atomicVerify = FileTools.ReadFileAsync(atomicFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(atomicVerify.Response.Contains("one two three"), "MultiEdit: file unchanged after atomic failure");

		// Duplicate match fails atomically.
		string dupeFile = Path.Combine(tempDir, "multi_dupe.txt");
		FileTools.WriteFileAsync(dupeFile, "aaa bbb aaa", tc).GetAwaiter().GetResult();
		JsonArray dupeEdits = new JsonArray
		{
			new JsonObject { ["oldContent"] = "aaa", ["newContent"] = "ccc" }
		};
		ToolResult dupeResult = FileTools.MultiEditFileAsync(dupeFile, dupeEdits, tc).GetAwaiter().GetResult();
		ctx.Assert(dupeResult.Response.Contains("Error") && dupeResult.Response.Contains("multiple"), "MultiEdit: duplicate match returns error");

		// Empty edits array.
		ToolResult emptyEdits = FileTools.MultiEditFileAsync(multiEditFile, new JsonArray(), tc).GetAwaiter().GetResult();
		ctx.Assert(emptyEdits.Response.Contains("Error"), "MultiEdit: empty edits returns error");

		// Read-first guard.
		string unreadFile = Path.Combine(tempDir, "unread_multi.txt");
		File.WriteAllText(unreadFile, "content");
		JsonArray unreadEdits = new JsonArray
		{
			new JsonObject { ["oldContent"] = "content", ["newContent"] = "changed" }
		};
		ToolResult unreadResult = FileTools.MultiEditFileAsync(unreadFile, unreadEdits, tc).GetAwaiter().GetResult();
		ctx.Assert(unreadResult.Response.Contains("Error") && unreadResult.Response.Contains("read_file"), "MultiEdit: unread file returns read-first error");

		// Supports snake_case property names from LLM.
		string snakeFile = Path.Combine(tempDir, "snake_edit.txt");
		FileTools.WriteFileAsync(snakeFile, "hello world", tc).GetAwaiter().GetResult();
		JsonArray snakeEdits = new JsonArray
		{
			new JsonObject { ["old_content"] = "hello", ["new_content"] = "hi" }
		};
		ToolResult snakeResult = FileTools.MultiEditFileAsync(snakeFile, snakeEdits, tc).GetAwaiter().GetResult();
		ctx.Assert(snakeResult.Response.Contains("1 edit(s) applied"), "MultiEdit: snake_case property names work");
	}

	private static void TestReadFirstGuard(TestContext ctx, ToolContext tc, string tempDir)
	{
		// Overwriting an existing file without reading it first is an error.
		string guardFile = Path.Combine(tempDir, "guard.txt");
		File.WriteAllText(guardFile, "original");
		ToolResult overwriteNoRead = FileTools.WriteFileAsync(guardFile, "replaced", tc).GetAwaiter().GetResult();
		ctx.Assert(overwriteNoRead.Response.Contains("Error") && overwriteNoRead.Response.Contains("read_file"), "FileTools: overwrite without read returns error");

		// After reading, overwrite succeeds.
		FileTools.ReadFileAsync(guardFile, "", "", tc).GetAwaiter().GetResult();
		ToolResult overwriteAfterRead = FileTools.WriteFileAsync(guardFile, "replaced", tc).GetAwaiter().GetResult();
		ctx.Assert(overwriteAfterRead.Response.Contains("File written"), "FileTools: overwrite after read succeeds");

		// Editing a file that was never read is an error.
		string unreadFile = Path.Combine(tempDir, "unread.txt");
		File.WriteAllText(unreadFile, "some content");
		ToolResult editNoRead = FileTools.EditFileAsync(unreadFile, "some", "other", false, tc).GetAwaiter().GetResult();
		ctx.Assert(editNoRead.Response.Contains("Error") && editNoRead.Response.Contains("read_file"), "FileTools: edit without read returns error");
	}

	private static void TestEdgeErrors(TestContext ctx, ToolContext tc, string tempDir)
	{
		// Empty path on write.
		ToolResult emptyPath = FileTools.WriteFileAsync("", "content", tc).GetAwaiter().GetResult();
		ctx.Assert(emptyPath.Response.Contains("Error"), "FileTools: write empty path returns error");

		// Read non-existent file.
		string ghostFile = Path.Combine(tempDir, "nonexistent.txt");
		ToolResult noFile = FileTools.ReadFileAsync(ghostFile, "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(noFile.Response.Contains("Error") && noFile.Response.Contains("not found"), "FileTools: read non-existent returns error");

		// Edit with empty oldContent.
		string edgeFile = Path.Combine(tempDir, "edit_edge.txt");
		FileTools.WriteFileAsync(edgeFile, "content", tc).GetAwaiter().GetResult();
		ToolResult emptyOld = FileTools.EditFileAsync(edgeFile, "", "new", false, tc).GetAwaiter().GetResult();
		ctx.Assert(emptyOld.Response.Contains("Error"), "FileTools: edit empty oldContent returns error");

		// Edit non-existent file (never read → read-first error).
		string missingFile = Path.Combine(tempDir, "ghost.txt");
		ToolResult editMissing = FileTools.EditFileAsync(missingFile, "old", "new", false, tc).GetAwaiter().GetResult();
		ctx.Assert(editMissing.Response.Contains("Error") && editMissing.Response.Contains("read_file"), "FileTools: edit unread file returns read-first error");

		// Write creates subdirectory automatically.
		string nestedFile = Path.Combine(tempDir, "newdir", "nested.txt");
		ToolResult subdir = FileTools.WriteFileAsync(nestedFile, "hi", tc).GetAwaiter().GetResult();
		ctx.Assert(subdir.Response.Contains("File written"), "FileTools: write creates subdirectory");

		// Empty read path.
		ToolResult emptyReadPath = FileTools.ReadFileAsync("", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(emptyReadPath.Response.Contains("Error"), "FileTools: read empty path returns error");
	}
}
