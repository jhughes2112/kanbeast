using System.Text.Json.Nodes;
using KanBeast.Shared;
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
			WorkerSession.Start(null!, null!, null!, null!, tempDir, CancellationToken.None, null!, string.Empty, string.Empty, new KanBeast.Shared.WebSearchConfig(), new KanBeast.Shared.CompactionSettings());
			ToolContext tc = new ToolContext(null, null, null);

			TestWriteAndRead(ctx, tc, tempDir);
			TestCatNFormat(ctx, tc, tempDir);
			TestReadWithOffsetLines(ctx, tc, tempDir);
         TestEditFile(ctx, tc, tempDir);
			TestEditToolVarious(ctx, tc, tempDir);
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

         // Read it back — always cat -n format. It now includes a per-line anchor after the number.
			ToolResult readResult = FileTools.ReadFileAsync(testFile, "", "", tc).GetAwaiter().GetResult();
			ctx.Assert(readResult.Response.Contains("hello world"), "FileTools: read returns content");
			ctx.Assert(readResult.Response.Contains("1:"), "FileTools: read includes line anchor");
			ctx.Assert(readResult.Response.Contains("\thello world"), "FileTools: read includes tab followed by content");

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
           ctx.Assert(readResult.Response.Contains("1:"), "CatN: line 1 present with anchor");
			ctx.Assert(readResult.Response.Contains("\tfirst"), "CatN: line 1 content present");
			ctx.Assert(readResult.Response.Contains("2:"), "CatN: blank line 2 preserved with anchor");
			ctx.Assert(readResult.Response.Contains("\t"), "CatN: blank line preserved");
			ctx.Assert(readResult.Response.Contains("3:"), "CatN: line 3 present with anchor");
			ctx.Assert(readResult.Response.Contains("\tthird"), "CatN: line 3 content present");
			ctx.Assert(readResult.Response.Contains("4:"), "CatN: line 4 present with anchor");
			ctx.Assert(readResult.Response.Contains("\tfourth"), "CatN: line 4 content present");
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
			ctx.Assert(windowed.Response.Contains("3:"), "FileTools: windowed read starts at correct offset (anchor present)");
			ctx.Assert(windowed.Response.Contains("\tline3"), "FileTools: windowed read includes line3 content");
			ctx.Assert(windowed.Response.Contains("6:"), "FileTools: windowed read ends at correct line (anchor present)");
			ctx.Assert(windowed.Response.Contains("\tline6"), "FileTools: windowed read includes line6 content");
			ctx.Assert(!windowed.Response.Contains("2:\tline2"), "FileTools: windowed read excludes line before offset");
			ctx.Assert(!windowed.Response.Contains("7:\tline7"), "FileTools: windowed read excludes line after range");
			ctx.Assert(windowed.Response.Contains("Showing lines 3-6"), "FileTools: windowed read has correct header");

		// Blank offset defaults to 1, lines=3 → lines 1-3.
		ToolResult blankOffset = FileTools.ReadFileAsync(multiFile, "", "3", tc).GetAwaiter().GetResult();
         ctx.Assert(blankOffset.Response.Contains("1:"), "FileTools: blank offset defaults to start (anchor)");
			ctx.Assert(blankOffset.Response.Contains("\tline1"), "FileTools: blank offset content present");
		ctx.Assert(blankOffset.Response.Contains("Showing lines 1-3"), "FileTools: blank offset header correct");

		// offset=7, blank lines → read up to MaxLines from line 7 → lines 7-10.
           ToolResult blankLines = FileTools.ReadFileAsync(multiFile, "7", "", tc).GetAwaiter().GetResult();
			ctx.Assert(blankLines.Response.Contains("7:"), "FileTools: 1-based offset reads correct line (anchor)");
			ctx.Assert(blankLines.Response.Contains("\tline7"), "FileTools: 1-based offset reads correct line (content)");
			ctx.Assert(blankLines.Response.Contains("10:"), "FileTools: blank lines includes last line (anchor)");
			ctx.Assert(blankLines.Response.Contains("\tline10"), "FileTools: blank lines includes last line (content)");
		ctx.Assert(blankLines.Response.Contains("Showing lines 7-10"), "FileTools: blank lines header correct");

		// Both blank → up to 10,000 lines, no header.
		ToolResult fullFile = FileTools.ReadFileAsync(multiFile, "", "", tc).GetAwaiter().GetResult();
            ctx.Assert(fullFile.Response.Contains("1:"), "FileTools: full read starts at line 1 anchor");
			ctx.Assert(fullFile.Response.Contains("\tline1"), "FileTools: full read starts at line 1 content");
			ctx.Assert(fullFile.Response.Contains("10:"), "FileTools: full read includes last line anchor");
			ctx.Assert(fullFile.Response.Contains("\tline10"), "FileTools: full read includes last line content");
		ctx.Assert(!fullFile.Response.Contains("Showing"), "FileTools: full read has no header");

		// offset=0, lines=0 → same as both blank (0 treated as 1).
		ToolResult zeros = FileTools.ReadFileAsync(multiFile, "0", "0", tc).GetAwaiter().GetResult();
         ctx.Assert(zeros.Response.Contains("1:"), "FileTools: explicit zeros returns full file anchor");
			ctx.Assert(zeros.Response.Contains("\tline1"), "FileTools: explicit zeros returns full file content");
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
           ctx.Assert(beyondLines.Response.Contains("8:"), "FileTools: lines beyond file starts correct (anchor)");
			ctx.Assert(beyondLines.Response.Contains("\tline8"), "FileTools: lines beyond file starts correct (content)");
			ctx.Assert(beyondLines.Response.Contains("10:"), "FileTools: lines beyond file includes last line (anchor)");
			ctx.Assert(beyondLines.Response.Contains("\tline10"), "FileTools: lines beyond file includes last line (content)");
	}

	private static void TestEditFile(TestContext ctx, ToolContext tc, string tempDir)
	{
		string editFile = Path.Combine(tempDir, "editable.txt");

		// Write initial content (new file — tracked by write).
		FileTools.WriteFileAsync(editFile, "alpha beta gamma", tc).GetAwaiter().GetResult();

          // New Edit API: replace the entire line using anchors. Read back to obtain anchor for line 1.
			ToolResult initialRead = FileTools.ReadFileAsync(editFile, "", "", tc).GetAwaiter().GetResult();
			string? anchorLine1 = null;
			{
				string[] parts = initialRead.Response.Split('\n');
				foreach (string ln in parts)
				{
					if (ln.Contains("alpha beta gamma"))
					{
						string prefix = ln.Split('\t')[0].Trim();
						anchorLine1 = prefix;
						break;
					}
				}
			}
			ctx.Assert(!string.IsNullOrEmpty(anchorLine1), "Test setup: obtained anchor for line 1");

			JsonArray replaceOp = new JsonArray
			{
				new JsonObject { ["replace_lines"] = new JsonObject { ["start_anchor"] = anchorLine1, ["end_anchor"] = anchorLine1, ["new_text"] = "alpha delta gamma" } }
			};
			ToolResult editResult = FileTools.EditFileAsync(editFile, replaceOp.ToJsonString(), tc).GetAwaiter().GetResult();
			ctx.Assert(editResult.Response.Contains("File edited"), "FileTools: edit succeeds using replace_lines");

			ToolResult verifyResult = FileTools.ReadFileAsync(editFile, "", "", tc).GetAwaiter().GetResult();
			ctx.Assert(verifyResult.Response.Contains("alpha delta gamma"), "FileTools: edit applied correctly (line replacement)");

			// Mismatched anchor should fail atomically.
			string badAnchor = anchorLine1!.Split(':')[0] + ":00"; // change hex to 00
			JsonArray badEdits = new JsonArray
			{
				new JsonObject { ["replace_lines"] = new JsonObject { ["start_anchor"] = badAnchor, ["end_anchor"] = badAnchor, ["new_text"] = "will not apply" } }
			};
			ToolResult badResult = FileTools.EditFileAsync(editFile, badEdits.ToJsonString(), tc).GetAwaiter().GetResult();
			ctx.Assert(badResult.Response.Contains("Error: One or more anchor hashes did not match"), "FileTools: mismatched anchor returns informative error");
	}

  private static void TestEditToolVarious(TestContext ctx, ToolContext tc, string tempDir)
	{
		// Write initial content.
		string multiEditFile = Path.Combine(tempDir, "multi_edit.txt");
		FileTools.WriteFileAsync(multiEditFile, "alpha beta gamma delta", tc).GetAwaiter().GetResult();

         // Now test insert_after and multiple operations behavior.
			string insertFile = Path.Combine(tempDir, "insert_test.txt");
			FileTools.WriteFileAsync(insertFile, "one\ntwo\nthree", tc).GetAwaiter().GetResult();
			ToolResult readInsert = FileTools.ReadFileAsync(insertFile, "", "", tc).GetAwaiter().GetResult();
          string? anchorLine2 = null;
		{
				string[] parts = readInsert.Response.Split('\n');
				foreach (string ln in parts)
				{
					if (ln.Contains("two"))
					{
						anchorLine2 = ln.Split('\t')[0].Trim();
						break;
					}
				}
			}
			ctx.Assert(!string.IsNullOrEmpty(anchorLine2), "Test setup: obtained anchor for line 2");

			JsonArray insOps = new JsonArray
			{
				new JsonObject { ["insert_after"] = new JsonObject { ["anchor"] = anchorLine2, ["new_text"] = "inserted" } }
			};
			ToolResult insResult = FileTools.EditFileAsync(insertFile, insOps.ToJsonString(), tc).GetAwaiter().GetResult();
			ctx.Assert(insResult.Response.Contains("File edited"), "EditFile: insert_after applied");
			ToolResult insVerify = FileTools.ReadFileAsync(insertFile, "", "", tc).GetAwaiter().GetResult();
			ctx.Assert(insVerify.Response.Contains("two"), "Insert verify: original line present");
			ctx.Assert(insVerify.Response.Contains("inserted"), "Insert verify: new line present");

			// Atomic failure with multiple ops: first op would replace line1, second op has bad anchor → expect no change
			string atomicFile = Path.Combine(tempDir, "atomic_edit.txt");
			FileTools.WriteFileAsync(atomicFile, "one two three", tc).GetAwaiter().GetResult();
			ToolResult atomicRead = FileTools.ReadFileAsync(atomicFile, "", "", tc).GetAwaiter().GetResult();
			string? anchor1 = null;
			{
				string[] parts = atomicRead.Response.Split('\n');
				foreach (string ln in parts)
				{
					if (ln.Contains("one two three"))
					{
						anchor1 = ln.Split('\t')[0].Trim();
						break;
					}
				}
			}
			string badAnchor2 = "2:00";
			JsonArray atomicOps = new JsonArray
			{
				new JsonObject { ["replace_lines"] = new JsonObject { ["start_anchor"] = anchor1, ["end_anchor"] = anchor1, ["new_text"] = "ONE" } },
				new JsonObject { ["replace_lines"] = new JsonObject { ["start_anchor"] = badAnchor2, ["end_anchor"] = badAnchor2, ["new_text"] = "BAD" } }
			};
			ToolResult atomicResult = FileTools.EditFileAsync(atomicFile, atomicOps.ToJsonString(), tc).GetAwaiter().GetResult();
			ctx.Assert(atomicResult.Response.Contains("Error"), "EditFile: atomic failure returns error");
			ToolResult atomicVerify = FileTools.ReadFileAsync(atomicFile, "", "", tc).GetAwaiter().GetResult();
			ctx.Assert(atomicVerify.Response.Contains("one two three"), "EditFile: file unchanged after atomic failure");
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

          // Editing a file that was never read is an error using new EditFileAsync signature.
			string unreadFile = Path.Combine(tempDir, "unread.txt");
			File.WriteAllText(unreadFile, "some content");
			JsonArray unreadEd = new JsonArray
			{
				new JsonObject { ["replace_lines"] = new JsonObject { ["start_anchor"] = "1:00", ["end_anchor"] = "1:00", ["new_text"] = "other" } }
			};
			ToolResult editNoRead = FileTools.EditFileAsync(unreadFile, unreadEd.ToJsonString(), tc).GetAwaiter().GetResult();
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

          // Edit with empty edits array should return error.
			string edgeFile = Path.Combine(tempDir, "edit_edge.txt");
			FileTools.WriteFileAsync(edgeFile, "content", tc).GetAwaiter().GetResult();
			ToolResult emptyEdits = FileTools.EditFileAsync(edgeFile, "[]", tc).GetAwaiter().GetResult();
			ctx.Assert(emptyEdits.Response.Contains("Error"), "FileTools: empty edits returns error");

			// Edit non-existent file (never read → read-first error) using new signature.
			string missingFile = Path.Combine(tempDir, "ghost.txt");
			JsonArray missingEdits = new JsonArray
			{
				new JsonObject { ["replace_lines"] = new JsonObject { ["start_anchor"] = "1:00", ["end_anchor"] = "1:00", ["new_text"] = "new" } }
			};
			ToolResult editMissing = FileTools.EditFileAsync(missingFile, missingEdits.ToJsonString(), tc).GetAwaiter().GetResult();
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
