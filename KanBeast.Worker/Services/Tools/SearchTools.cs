using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace KanBeast.Worker.Services.Tools;

// Tools for searching files and content in the repository.
public static class SearchTools
{
	[Description("""
		Finds files by name pattern. Returns one relative file path per line, sorted by most recently modified first, with a count header. 
		Skips the directories that start with dot.
		Use this instead of run_command with find. Prefer grep when searching file contents rather than file names.
		- Fast file pattern matching tool that works with any codebase size
		- Supports glob patterns and recursion
		- Returns matching file paths sorted by modification time
		- Use this tool when you need to find files by name patterns
		- When you are doing an open ended search that may require multiple rounds of globbing and grepping, use the Agent tool instead
		- You have the capability to call multiple tools in a single response. It is always better to speculatively perform multiple searches as a batch that are potentially useful.
		""")]
	public static Task<ToolResult> GlobAsync(
		[Description("""
			Glob pattern to match against relative file paths. 
			Syntax: ** matches any directory depth, * matches any chars within a filename, ? matches one char, {a,b} matches alternates. 
			Examples: '**/*.cs' (all C# files recursive), 'src/**/*.ts' (all TypeScript under src/), '*.{json,yaml}' (root-level config files).
			""")] string pattern,
		[Description("Absolute full path to the directory to search from.")] string path,
		ToolContext context)
	{
		CancellationToken cancellationToken = WorkerSession.CancellationToken;
		ToolResult result;

		if (string.IsNullOrWhiteSpace(pattern))
		{
			result = new ToolResult("Error: Pattern cannot be empty", false, false);
		}
		else if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
		{
			result = new ToolResult($"Error: Path must be an absolute directory path: {path}", false, false);
		}
		else
		{
			string searchDir = Path.GetFullPath(path);

			if (!Directory.Exists(searchDir))
			{
				result = new ToolResult($"Error: Directory not found: {path}", false, false);
			}
			else
			{
				try
				{
					Regex globRegex = GlobToRegex(pattern);
					List<(string RelativePath, DateTime Modified)> matches = new List<(string, DateTime)>();
					List<string> files = new List<string>();
					CollectFiles(searchDir, files, cancellationToken);

					foreach (string filePath in files)
					{
						string relativePath = Path.GetRelativePath(searchDir, filePath).Replace('\\', '/');

						if (globRegex.IsMatch(relativePath))
						{
							DateTime modified = File.GetLastWriteTimeUtc(filePath);
							matches.Add((relativePath, modified));
						}
					}

					matches.Sort((a, b) => b.Modified.CompareTo(a.Modified));

					if (matches.Count == 0)
					{
						result = new ToolResult($"No files found matching: {pattern}", false, false);
					}
					else
					{
						StringBuilder sb = new StringBuilder();
						sb.AppendLine($"{matches.Count} file(s) matching {pattern}:");

						foreach ((string relativePath, DateTime _) in matches)
						{
							sb.AppendLine(relativePath);
						}

						result = new ToolResult(sb.ToString(), false, false);
					}
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: {ex.Message}", false, false);
				}
			}
		}

		return Task.FromResult(result);
	}

	[Description("""
		Lists files and directories in a given path. The path parameter must be an absolute path, not a relative path. 
		You can optionally provide an array of glob patterns to ignore with the ignore parameter. 
		You should generally prefer the Glob and Grep tools, if you know which directories to search.
		Directory names end with '/'. Returns an error if the directory does not exist.
		""")]
	public static Task<ToolResult> ListDirectoryAsync(
		[Description("The absolute path to the directory to list.")] string path,
		[Description("List of glob patterns to ignore")] string[] ignore,
		ToolContext context)
	{
		CancellationToken cancellationToken = WorkerSession.CancellationToken;
		ToolResult result;

		if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
		{
			result = new ToolResult($"Error: Path must be an absolute directory path: {path}", false, false);
		}
		else
		{
			string targetDir = Path.GetFullPath(path);

			if (!Directory.Exists(targetDir))
			{
				result = new ToolResult($"Error: Directory not found: {path}", false, false);
			}
			else
			{
				try
				{
					List<Regex> ignorePatterns = new List<Regex>();
					if (ignore != null && ignore.Length > 0)
					{
						foreach (string pattern in ignore)
						{
							if (!string.IsNullOrWhiteSpace(pattern))
							{
								ignorePatterns.Add(GlobToRegex(pattern));
							}
						}
					}

					StringBuilder sb = new StringBuilder();
					string[] directories = Directory.GetDirectories(targetDir);
					string[] files = Directory.GetFiles(targetDir);

					Array.Sort(directories);
					Array.Sort(files);

					foreach (string dir in directories)
					{
						string dirName = Path.GetFileName(dir);
						bool ignored = false;

						foreach (Regex ignorePattern in ignorePatterns)
						{
							if (ignorePattern.IsMatch(dirName) || ignorePattern.IsMatch(dirName + "/"))
							{
								ignored = true;
								break;
							}
						}

						if (!ignored)
						{
							sb.AppendLine($"{dirName}/");
						}
					}

					foreach (string file in files)
					{
						string fileName = Path.GetFileName(file);
						bool ignored = false;

						foreach (Regex ignorePattern in ignorePatterns)
						{
							if (ignorePattern.IsMatch(fileName))
							{
								ignored = true;
								break;
							}
						}

						if (!ignored)
						{
							sb.AppendLine(fileName);
						}
					}

					if (sb.Length == 0)
					{
						result = new ToolResult($"Directory is empty: {path}", false, false);
					}
					else
					{
						result = new ToolResult(sb.ToString(), false, false);
					}
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: {ex.Message}", false, false);
				}
			}
		}

		return Task.FromResult(result);
	}

	[Description("""
		Search file contents using regex. Skips dot-directories.
		Output modes: 'files_with_matches' (default, file paths), 'content' (matching lines with line numbers and context), 'count' (match counts per file).
		Use instead of grep/rg via run_command. Use maxResults to limit broad searches.
		""")]
	public static Task<ToolResult> GrepAsync(
		[Description("Regular expression to search for (e.g. 'class\\s+Foo', 'TODO', 'import.*http'). Standard regex syntax. Cannot be empty.")] string pattern,
		[Description("Absolute path to a directory to search recursively, or a single file to search. Must be a rooted path. Cannot be empty.")] string path,
		[Description("Glob pattern to filter which files are searched (e.g. '*.cs', '*.{ts,tsx}'). Patterns without a / are automatically matched at any directory depth. Pass empty string to search all files.")] string include,
		[Description("Output mode. Pass 'files_with_matches' for file paths only (default), 'content' for matching lines with line numbers, or 'count' for match counts per file. Pass empty string for the default.")] string outputMode,
		[Description("Number of context lines to show before and after each match. Only used when outputMode is 'content'; ignored otherwise. Pass empty string for 0.")] string context,
		[Description("Pass 'true' for case-insensitive matching. Pass empty string for case-sensitive (the default).")] string caseInsensitive,
		[Description("Limit output to first N lines/entries, equivalent to \"| head -N\". Works across all output modes: content (limits output lines), files_with_matches (limits file paths), count (limits count entries). Pass empty string for no limit.")] string maxResults,
		ToolContext toolContext)
	{
		CancellationToken cancellationToken = WorkerSession.CancellationToken;
		ToolResult result;

		if (string.IsNullOrWhiteSpace(pattern))
		{
			result = new ToolResult("Error: Pattern cannot be empty", false, false);
		}
		else if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
		{
			result = new ToolResult($"Error: Path must be absolute: {path}", false, false);
		}
		else
		{
			string searchPath = Path.GetFullPath(path);

			bool isFile = File.Exists(searchPath);
			bool isDir = Directory.Exists(searchPath);

			if (!isFile && !isDir)
			{
				result = new ToolResult($"Error: Path not found: {path}", false, false);
			}
			else
			{
				try
				{
					RegexOptions regexOpts = RegexOptions.Compiled;

					if (string.Equals(caseInsensitive, "true", StringComparison.OrdinalIgnoreCase))
					{
						regexOpts |= RegexOptions.IgnoreCase;
					}

					Regex searchRegex = new Regex(pattern, regexOpts);

					string effectiveMode = string.IsNullOrWhiteSpace(outputMode) ? "files_with_matches" : outputMode;

					int contextLines = 0;

					if (!string.IsNullOrWhiteSpace(context))
					{
						int.TryParse(context, out contextLines);
					}

					int limit = 0;

					if (!string.IsNullOrWhiteSpace(maxResults))
					{
						int.TryParse(maxResults, out limit);
					}

					// Build include filter. Bare patterns like *.cs match at any depth.
					Regex? includeRegex = null;

					if (!string.IsNullOrWhiteSpace(include))
					{
						string normalizedInclude = include;

						if (!normalizedInclude.Contains('/') && !normalizedInclude.Contains('\\') && !normalizedInclude.StartsWith("**"))
						{
							normalizedInclude = $"**/{normalizedInclude}";
						}

						includeRegex = GlobToRegex(normalizedInclude);
					}

					// Collect files to search.
					List<string> filesToSearch = new List<string>();

					if (isFile)
					{
						filesToSearch.Add(searchPath);
					}
					else
					{
						CollectFiles(searchPath, filesToSearch, cancellationToken);

						if (includeRegex != null)
						{
							List<string> filtered = new List<string>();

							foreach (string filePath in filesToSearch)
							{
								string relativePath = Path.GetRelativePath(searchPath, filePath).Replace('\\', '/');

								if (includeRegex.IsMatch(relativePath))
								{
									filtered.Add(filePath);
								}
							}

							filesToSearch = filtered;
						}
					}

					StringBuilder sb = new StringBuilder();
					int totalMatches = 0;
					int resultCount = 0;

					foreach (string filePath in filesToSearch)
					{
						if (cancellationToken.IsCancellationRequested)
						{
							break;
						}

						if (limit > 0 && resultCount >= limit)
						{
							break;
						}

						string[] fileLines;

						try
						{
							fileLines = File.ReadAllLines(filePath);
						}
						catch
						{
							continue;
						}

						// Find matching line indices.
						List<int> matchingLines = new List<int>();

						for (int i = 0; i < fileLines.Length; i++)
						{
							if (searchRegex.IsMatch(fileLines[i]))
							{
								matchingLines.Add(i);
							}
						}

						if (matchingLines.Count == 0)
						{
							continue;
						}

						string relativePath = isFile
							? Path.GetFileName(filePath)
							: Path.GetRelativePath(searchPath, filePath).Replace('\\', '/');

						if (string.Equals(effectiveMode, "files_with_matches", StringComparison.OrdinalIgnoreCase))
						{
							sb.AppendLine(relativePath);
							resultCount++;
						}
						else if (string.Equals(effectiveMode, "count", StringComparison.OrdinalIgnoreCase))
						{
							sb.AppendLine($"{relativePath}: {matchingLines.Count}");
							totalMatches += matchingLines.Count;
							resultCount++;
						}
						else
						{
							sb.AppendLine($"--- {relativePath} ---");

							// Build set of lines to display (matches + context).
							HashSet<int> linesToShow = new HashSet<int>();

							foreach (int lineIdx in matchingLines)
							{
								int contextStart = Math.Max(0, lineIdx - contextLines);
								int contextEnd = Math.Min(fileLines.Length - 1, lineIdx + contextLines);

								for (int c = contextStart; c <= contextEnd; c++)
								{
									linesToShow.Add(c);
								}
							}

							List<int> sortedLines = new List<int>(linesToShow);
							sortedLines.Sort();

							int previousLine = -2;

							foreach (int lineIdx in sortedLines)
							{
								if (lineIdx > previousLine + 1 && previousLine >= 0)
								{
									sb.AppendLine("...");
								}

								sb.AppendLine($"{lineIdx + 1}: {fileLines[lineIdx]}");
								previousLine = lineIdx;
							}

							resultCount++;
							sb.AppendLine();
						}
					}

					if (sb.Length == 0)
					{
						result = new ToolResult($"No matches found for: {pattern}", false, false);
					}
					else
					{
						if (string.Equals(effectiveMode, "count", StringComparison.OrdinalIgnoreCase))
						{
							sb.AppendLine($"Total: {totalMatches} matches");
						}

						result = new ToolResult(sb.ToString(), false, false);
					}
				}
				catch (RegexParseException ex)
				{
					result = new ToolResult($"Error: Invalid regex pattern: {ex.Message}", false, false);
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: {ex.Message}", false, false);
				}
			}
		}

		return Task.FromResult(result);
	}

	// Collect files recursively, skipping directories that start with dot (like .git, .vs, .vscode, etc).
	private static void CollectFiles(string rootDir, List<string> results, CancellationToken cancellationToken)
	{
		Stack<string> dirs = new Stack<string>();
		dirs.Push(rootDir);

		while (dirs.Count > 0)
		{
			if (cancellationToken.IsCancellationRequested)
			{
				break;
			}

			string currentDir = dirs.Pop();

			try
			{
				foreach (string file in Directory.GetFiles(currentDir))
				{
					results.Add(file);
				}
			}
			catch
			{
			}

			try
			{
				foreach (string subdir in Directory.GetDirectories(currentDir))
				{
					string dirName = Path.GetFileName(subdir);

					if (!dirName.StartsWith('.'))
					{
						dirs.Push(subdir);
					}
				}
			}
			catch
			{
			}
		}
	}

	// Convert a glob pattern to a compiled Regex.
	private static Regex GlobToRegex(string glob)
	{
		StringBuilder sb = new StringBuilder();
		sb.Append('^');

		int i = 0;
		int braceDepth = 0;

		while (i < glob.Length)
		{
			char c = glob[i];

			if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
			{
				// ** matches any directory depth.
				if (i + 2 < glob.Length && (glob[i + 2] == '/' || glob[i + 2] == '\\'))
				{
					sb.Append("(.+/)?");
					i += 3;
				}
				else
				{
					sb.Append(".*");
					i += 2;
				}
			}
			else if (c == '*')
			{
				sb.Append("[^/\\\\]*");
				i++;
			}
			else if (c == '?')
			{
				sb.Append("[^/\\\\]");
				i++;
			}
			else if (c == '{')
			{
				sb.Append('(');
				braceDepth++;
				i++;
			}
			else if (c == '}')
			{
				sb.Append(')');
				braceDepth--;
				i++;
			}
			else if (c == ',' && braceDepth > 0)
			{
				sb.Append('|');
				i++;
			}
			else if (c == '/' || c == '\\')
			{
				sb.Append("[/\\\\]");
				i++;
			}
			else if (".+^$|[]()".Contains(c))
			{
				sb.Append('\\');
				sb.Append(c);
				i++;
			}
			else
			{
				sb.Append(c);
				i++;
			}
		}

		sb.Append('$');

		return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Compiled);
	}
}
