using System.Text.RegularExpressions;
using KanBeast.Shared;
using KanBeast.Worker.Services;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Tests;

public static class SearchToolsTests
{
	public static void Test(TestContext ctx)
	{
		Console.WriteLine("  SearchToolsTests");

		string tempDir = Path.Combine(Path.GetTempPath(), $"kanbeast_search_{Guid.NewGuid():N}");
		Directory.CreateDirectory(tempDir);

		try
			{
				CreateTestFiles(tempDir);

				WorkerSession.Start(null!, null!, null!, null!, tempDir, CancellationToken.None, null!, new KanBeast.Shared.WebSearchConfig());
				ConversationMemories testMemories = new ConversationMemories();
				ToolContext tc = new ToolContext(null, null, testMemories);

				TestGlobToRegex(ctx);
				TestGlob(ctx, tc, tempDir);
				TestListDirectory(ctx, tc, tempDir);
				TestGrep(ctx, tc, tempDir);
			}
		finally
		{
			if (Directory.Exists(tempDir))
			{
				Directory.Delete(tempDir, true);
			}
		}
	}

	private static void CreateTestFiles(string tempDir)
	{
		File.WriteAllText(Path.Combine(tempDir, "hello.cs"), "public class Hello\n{\n    public void Say() { }\n}\n");
		File.WriteAllText(Path.Combine(tempDir, "readme.md"), "# Test Project\nThis is a test.\n");

		string srcDir = Path.Combine(tempDir, "src");
		Directory.CreateDirectory(srcDir);
		File.WriteAllText(Path.Combine(srcDir, "app.cs"), "public class App\n{\n    public void Run() { }\n}\n");
		File.WriteAllText(Path.Combine(srcDir, "app.ts"), "export class App {\n  run() { }\n}\n");

		string utilsDir = Path.Combine(srcDir, "utils");
		Directory.CreateDirectory(utilsDir);
		File.WriteAllText(Path.Combine(utilsDir, "helper.cs"), "public class Helper\n{\n    public int Add(int a, int b) { return a + b; }\n}\n");
	}

	private static void TestGlobToRegex(TestContext ctx)
	{
		Type[] types = [typeof(string)];

		// ** matches any depth.
		Regex deepRegex = (Regex)Reflect.Static(typeof(SearchTools), "GlobToRegex", types, ["**/*.cs"])!;
		ctx.Assert(deepRegex.IsMatch("hello.cs"), "GlobToRegex: **/*.cs matches root file");
		ctx.Assert(deepRegex.IsMatch("src/app.cs"), "GlobToRegex: **/*.cs matches nested file");
		ctx.Assert(deepRegex.IsMatch("a/b/c/deep.cs"), "GlobToRegex: **/*.cs matches deeply nested file");
		ctx.Assert(!deepRegex.IsMatch("hello.ts"), "GlobToRegex: **/*.cs rejects wrong extension");

		// Single * does not cross directories.
		Regex shallowRegex = (Regex)Reflect.Static(typeof(SearchTools), "GlobToRegex", types, ["*.cs"])!;
		ctx.Assert(shallowRegex.IsMatch("hello.cs"), "GlobToRegex: *.cs matches root file");
		ctx.Assert(!shallowRegex.IsMatch("src/hello.cs"), "GlobToRegex: *.cs rejects nested file");

		// Brace alternation.
		Regex braceRegex = (Regex)Reflect.Static(typeof(SearchTools), "GlobToRegex", types, ["*.{cs,ts}"])!;
		ctx.Assert(braceRegex.IsMatch("hello.cs"), "GlobToRegex: {cs,ts} matches .cs");
		ctx.Assert(braceRegex.IsMatch("hello.ts"), "GlobToRegex: {cs,ts} matches .ts");
		ctx.Assert(!braceRegex.IsMatch("hello.py"), "GlobToRegex: {cs,ts} rejects .py");

		// Subdirectory prefix.
		Regex prefixRegex = (Regex)Reflect.Static(typeof(SearchTools), "GlobToRegex", types, ["src/**/*.cs"])!;
		ctx.Assert(prefixRegex.IsMatch("src/app.cs"), "GlobToRegex: src/**/*.cs matches direct child");
		ctx.Assert(prefixRegex.IsMatch("src/utils/helper.cs"), "GlobToRegex: src/**/*.cs matches nested child");
		ctx.Assert(!prefixRegex.IsMatch("hello.cs"), "GlobToRegex: src/**/*.cs rejects root file");

		// ? matches single character.
		Regex questionRegex = (Regex)Reflect.Static(typeof(SearchTools), "GlobToRegex", types, ["?.cs"])!;
		ctx.Assert(questionRegex.IsMatch("a.cs"), "GlobToRegex: ?.cs matches single char");
		ctx.Assert(!questionRegex.IsMatch("ab.cs"), "GlobToRegex: ?.cs rejects two chars");
	}

	private static void TestGlob(TestContext ctx, ToolContext tc, string tempDir)
	{
		string srcDir = Path.Combine(tempDir, "src");

		// Find all .cs files recursively.
		ToolResult allCs = SearchTools.GlobAsync("**/*.cs", tempDir, tc).GetAwaiter().GetResult();
		ctx.Assert(allCs.Response.Contains("hello.cs"), "Glob: finds root .cs file");
		ctx.Assert(allCs.Response.Contains("src/app.cs"), "Glob: finds nested .cs file");
		ctx.Assert(allCs.Response.Contains("src/utils/helper.cs"), "Glob: finds deeply nested .cs file");
		ctx.Assert(!allCs.Response.Contains("app.ts"), "Glob: excludes non-.cs files");

		// Find .ts files.
		ToolResult tsFiles = SearchTools.GlobAsync("**/*.ts", tempDir, tc).GetAwaiter().GetResult();
		ctx.Assert(tsFiles.Response.Contains("app.ts"), "Glob: finds .ts file");

		// Non-recursive pattern.
		ToolResult shallowCs = SearchTools.GlobAsync("*.cs", tempDir, tc).GetAwaiter().GetResult();
		ctx.Assert(shallowCs.Response.Contains("hello.cs"), "Glob: shallow pattern finds root file");
		ctx.Assert(!shallowCs.Response.Contains("src/app.cs"), "Glob: shallow pattern skips nested files");

		// Scoped to subdirectory.
		ToolResult srcOnly = SearchTools.GlobAsync("**/*.cs", srcDir, tc).GetAwaiter().GetResult();
		ctx.Assert(srcOnly.Response.Contains("app.cs"), "Glob: scoped to subdirectory finds files");
		ctx.Assert(!srcOnly.Response.Contains("hello.cs"), "Glob: scoped to subdirectory excludes parent files");

		// No matches.
		ToolResult noMatch = SearchTools.GlobAsync("**/*.xyz", tempDir, tc).GetAwaiter().GetResult();
		ctx.Assert(noMatch.Response.Contains("No files found"), "Glob: no matches returns message");

		// Empty pattern.
		ToolResult emptyPattern = SearchTools.GlobAsync("", tempDir, tc).GetAwaiter().GetResult();
		ctx.Assert(emptyPattern.Response.Contains("Error"), "Glob: empty pattern returns error");

		// Empty path.
		ToolResult emptyPath = SearchTools.GlobAsync("**/*.cs", "", tc).GetAwaiter().GetResult();
		ctx.Assert(emptyPath.Response.Contains("Error"), "Glob: empty path returns error");
	}

	private static void TestListDirectory(TestContext ctx, ToolContext tc, string tempDir)
	{
		string srcDir = Path.Combine(tempDir, "src");

		// List root.
		ToolResult root = SearchTools.ListDirectoryAsync(tempDir, Array.Empty<string>(), tc).GetAwaiter().GetResult();
		ctx.Assert(root.Response.Contains("src/"), "ListDirectory: shows subdirectory with trailing slash");
		ctx.Assert(root.Response.Contains("hello.cs"), "ListDirectory: shows files");
		ctx.Assert(root.Response.Contains("readme.md"), "ListDirectory: shows all files");

		// List subdirectory.
		ToolResult src = SearchTools.ListDirectoryAsync(srcDir, Array.Empty<string>(), tc).GetAwaiter().GetResult();
		ctx.Assert(src.Response.Contains("app.cs"), "ListDirectory: lists subdirectory contents");
		ctx.Assert(src.Response.Contains("utils/"), "ListDirectory: shows nested subdirectory");

		// Test ignore patterns - ignore markdown files.
		ToolResult ignoreMarkdown = SearchTools.ListDirectoryAsync(tempDir, new[] { "*.md" }, tc).GetAwaiter().GetResult();
		ctx.Assert(ignoreMarkdown.Response.Contains("src/"), "ListDirectory: ignore keeps directories");
		ctx.Assert(ignoreMarkdown.Response.Contains("hello.cs"), "ListDirectory: ignore keeps non-matching files");
		ctx.Assert(!ignoreMarkdown.Response.Contains("readme.md"), "ListDirectory: ignore filters matching files");

		// Test ignore patterns - ignore directories.
		ToolResult ignoreDir = SearchTools.ListDirectoryAsync(tempDir, new[] { "src" }, tc).GetAwaiter().GetResult();
		ctx.Assert(!ignoreDir.Response.Contains("src/"), "ListDirectory: ignore filters matching directories");
		ctx.Assert(ignoreDir.Response.Contains("hello.cs"), "ListDirectory: ignore with dir keeps files");

		// Test multiple ignore patterns.
		ToolResult multiIgnore = SearchTools.ListDirectoryAsync(tempDir, new[] { "*.md", "src" }, tc).GetAwaiter().GetResult();
		ctx.Assert(!multiIgnore.Response.Contains("readme.md"), "ListDirectory: multi ignore filters files");
		ctx.Assert(!multiIgnore.Response.Contains("src/"), "ListDirectory: multi ignore filters directories");
		ctx.Assert(multiIgnore.Response.Contains("hello.cs"), "ListDirectory: multi ignore keeps unmatched");

		// Non-existent directory.
		string missingDir = Path.Combine(tempDir, "nonexistent");
		ToolResult missing = SearchTools.ListDirectoryAsync(missingDir, Array.Empty<string>(), tc).GetAwaiter().GetResult();
		ctx.Assert(missing.Response.Contains("Error"), "ListDirectory: non-existent returns error");

		// Empty path.
		ToolResult emptyPath = SearchTools.ListDirectoryAsync("", Array.Empty<string>(), tc).GetAwaiter().GetResult();
		ctx.Assert(emptyPath.Response.Contains("Error"), "ListDirectory: empty path returns error");
	}

	private static void TestGrep(TestContext ctx, ToolContext tc, string tempDir)
	{
		// files_with_matches mode (default).
		ToolResult filesMode = SearchTools.GrepAsync("class", tempDir, "", "", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(filesMode.Response.Contains("hello.cs"), "Grep: files_with_matches finds root match");
		ctx.Assert(filesMode.Response.Contains("src/app.cs"), "Grep: files_with_matches finds nested match");

		// Content mode shows matching lines.
		ToolResult contentMode = SearchTools.GrepAsync("class Hello", tempDir, "", "content", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(contentMode.Response.Contains("public class Hello"), "Grep: content mode shows matching line");
		ctx.Assert(contentMode.Response.Contains("hello.cs"), "Grep: content mode shows filename");

		// Content mode with context lines.
		ToolResult withContext = SearchTools.GrepAsync("void Run", tempDir, "**/*.cs", "content", "2", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(withContext.Response.Contains("Run"), "Grep: context mode includes match");
		ctx.Assert(withContext.Response.Contains("class App"), "Grep: context=2 shows class declaration line");

		// Count mode.
		ToolResult countMode = SearchTools.GrepAsync("class", tempDir, "**/*.cs", "count", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(countMode.Response.Contains("Total:"), "Grep: count mode shows total");

		// Case-insensitive search.
		ToolResult caseInsensitive = SearchTools.GrepAsync("CLASS", tempDir, "", "", "", "true", "", tc).GetAwaiter().GetResult();
		ctx.Assert(caseInsensitive.Response.Contains("hello.cs"), "Grep: case-insensitive finds matches");

		// Include filter restricts to matching files.
		ToolResult tsOnly = SearchTools.GrepAsync("class", tempDir, "*.ts", "", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(tsOnly.Response.Contains("app.ts"), "Grep: include filter matches .ts file");
		ctx.Assert(!tsOnly.Response.Contains(".cs"), "Grep: include filter excludes .cs files");

		// maxResults limits file count.
		ToolResult limited = SearchTools.GrepAsync("class", tempDir, "", "", "", "", "1", tc).GetAwaiter().GetResult();
		string[] limitedLines = limited.Response.Split('\n', StringSplitOptions.RemoveEmptyEntries);
		ctx.Assert(limitedLines.Length == 1, "Grep: maxResults=1 returns one file");

		// No matches.
		ToolResult noMatch = SearchTools.GrepAsync("zzzzzznonexistent", tempDir, "", "", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(noMatch.Response.Contains("No matches"), "Grep: no matches returns message");

		// Empty pattern.
		ToolResult emptyPattern = SearchTools.GrepAsync("", tempDir, "", "", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(emptyPattern.Response.Contains("Error"), "Grep: empty pattern returns error");

		// Invalid regex.
		ToolResult badRegex = SearchTools.GrepAsync("[invalid", tempDir, "", "", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(badRegex.Response.Contains("Error"), "Grep: invalid regex returns error");

		// Empty path.
		ToolResult emptyPath = SearchTools.GrepAsync("class", "", "", "", "", "", "", tc).GetAwaiter().GetResult();
		ctx.Assert(emptyPath.Response.Contains("Error"), "Grep: empty path returns error");
	}
}
