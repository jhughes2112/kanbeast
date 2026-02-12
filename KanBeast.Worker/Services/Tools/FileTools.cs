using System.ComponentModel;
using System.Text;
using System.Text.Json.Nodes;

namespace KanBeast.Worker.Services.Tools;

// Tools for LLM to read and write files.
public static class FileTools
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

	[Description("""
		Reads a file from the local filesystem. You can access any file directly by using this tool.
		Assume this tool is able to read all files on the machine. It is okay to read a file that does not exist; an error will be returned.

		Usage:
		- The file_path parameter must be an absolute path, not a relative path
		- By default, it reads up to 160k characters starting from the beginning of the file
		- You can optionally specify a line offset and limit (especially handy for long files), but it's recommended to read the whole file by not providing these parameters
		- If the output is truncated, it returns the first and last 80k and a warning in the middle that it was truncated and by how much
		- Results are returned using cat -n format, with line numbers starting at 1
		Use offset and lines to page through large files. Do not use cat, head, or tail via run_command; use this tool instead.
		""")]
    public static async Task<ToolResult> ReadFileAsync(
        [Description("Absolute path to the file to read.")] string filePath,
        [Description("The line number to start reading from (1 based). Only provide if the file is too large to read at once")] string offset,
        [Description("The number of lines to read. Only provide if the file is too large to read at once")] string lines,
        ToolContext context)
    {
        return await ReadFileContentAsync(filePath, offset, lines, context);
    }

    [Description("Alias for read_file.")] 
    public static async Task<ToolResult> GetFileAsync(
        [Description("Absolute path to the file to read.")] string filePath,
        [Description("The line number to start reading from (1 based). Only provide if the file is too large to read at once")] string offset,
        [Description("The number of lines to read. Only provide if the file is too large to read at once")] string lines,
        ToolContext context)
    {
        return await ReadFileContentAsync(filePath, offset, lines, context);
    }

    private static async Task<ToolResult> ReadFileContentAsync(string filePath, string offset, string lines, ToolContext context)
    {
        const int MaxLines = 2000;
        CancellationToken cancellationToken = WorkerSession.CancellationToken;

        ToolResult result;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            result = new ToolResult("Error: Path cannot be empty", false);
        }
        else if (!Path.IsPathRooted(filePath))
        {
            result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
        }
        else
        {
            string fullPath = Path.GetFullPath(filePath);

            try
            {
                if (!File.Exists(fullPath))
                {
                    result = new ToolResult($"Error: File not found: {filePath}", false);
                }
                else
                {
                    int offsetValue = 0;
                    bool offsetValid = string.IsNullOrWhiteSpace(offset) || int.TryParse(offset, out offsetValue);

                    int linesValue = 0;
                    bool linesValid = string.IsNullOrWhiteSpace(lines) || int.TryParse(lines, out linesValue);

                    if (!offsetValid)
                    {
                        result = new ToolResult($"Error: Invalid offset value: {offset}", false);
                    }
                    else if (!linesValid)
                    {
                        result = new ToolResult($"Error: Invalid lines value: {lines}", false);
                    }
                    else if (offsetValue < 0)
                    {
                        result = new ToolResult("Error: offset must be >= 0", false);
                    }
                    else if (linesValue < 0)
                    {
                        result = new ToolResult("Error: lines must be >= 0", false);
                    }
                    else
                    {
                        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        cts.CancelAfter(DefaultTimeout);

                        using FileStream fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using StreamReader sr = new StreamReader(fs);

                        context.ReadFiles.Add(fullPath);

                        // Offset is 1-based; 0 is treated as 1.
                        if (offsetValue <= 0)
                        {
                            offsetValue = 1;
                        }

                        int startLine = offsetValue;
                        int requestedLines = linesValue > 0 ? linesValue : MaxLines;
                        int linesToRead = Math.Min(requestedLines, MaxLines);

                        List<string> readLines = new List<string>();
                        int currentLine = 0;
                        bool hitMaxLines = false;

                        while (!sr.EndOfStream)
                        {
                            currentLine++;

                            string? line = await sr.ReadLineAsync(cts.Token);
                            if (line == null)
                            {
                                break;
                            }

                            if (currentLine >= startLine && readLines.Count < linesToRead)
                            {
                                readLines.Add(line);
                            }

                            if (currentLine >= startLine + linesToRead)
                            {
                                break;
                            }

                            if (currentLine >= MaxLines && startLine == 1 && linesValue == 0)
                            {
                                hitMaxLines = true;
                                break;
                            }
                        }

                        if (readLines.Count == 0)
                        {
                            if (currentLine == 0)
                            {
                                result = new ToolResult($"File is empty: {filePath}", false);
                            }
                            else
                            {
                                result = new ToolResult($"Offset {startLine} is beyond the end of the file (file has {currentLine} lines).", false);
                            }
                        }
                        else
                        {
                            int endLine = startLine + readLines.Count - 1;
                            bool isWindowed = offsetValue > 1 || linesValue > 0;

                            StringBuilder sb = new StringBuilder();

                            if (isWindowed)
                            {
                                sb.AppendLine($"Showing lines {startLine}-{endLine}:");
                            }

                            for (int i = 0; i < readLines.Count; i++)
                            {
                                sb.AppendLine($"{startLine + i,6}\t{readLines[i]}");
                            }

                            if (hitMaxLines)
                            {
                                sb.AppendLine();
                                sb.AppendLine($"[Output truncated at {MaxLines} lines. Use offset and lines parameters to read more.]");
                            }

                            result = new ToolResult(sb.ToString(), false);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = new ToolResult($"Error: Timed out or cancelled reading file: {filePath}", false);
            }
            catch (Exception ex)
            {
                result = new ToolResult($"Error: Failed to read file: {ex.Message}", false);
            }
        }

        return result;
    }

    [Description("""
		This tool will overwrite the existing file if there is one at the provided path. Caution: this creates the file and any missing parent directories if they do not exist. 
		If the file already exists, you must use read_file first; this tool will error if you attempt to overwrite without reading. 
		ALWAYS prefer editing existing files in the codebase. NEVER write new files unless explicitly required.
		Use this for new files or full rewrites. Prefer edit_file for changing part of an existing file.
		Only use emojis if the user explicitly requests it. Avoid writing emojis to files unless asked.
		NEVER create files that are not explicitly requested by the user or required to complete the task, including documentation (*.md) files.
		""")]
    public static async Task<ToolResult> WriteFileAsync(
        [Description("The exact full path to the file to create or overwrite. Absolute paths only.")] string filePath,
        [Description("The complete content to write to the file. This replaces the entire file contents.")] string content,
        ToolContext context)
    {
        ToolResult result;
        CancellationToken cancellationToken = WorkerSession.CancellationToken;

        if (string.IsNullOrWhiteSpace(filePath))
        {
            result = new ToolResult("Error: Path cannot be empty", false);
        }
        else if (!Path.IsPathRooted(filePath))
        {
            result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
        }
        else
        {
            string fullPath = Path.GetFullPath(filePath);

            if (File.Exists(fullPath) && !context.ReadFiles.Contains(fullPath))
            {
                result = new ToolResult($"Error: You must use read_file on {filePath} before overwriting it.", false);
            }
            else
            {
                try
                {
                    string? directory = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(DefaultTimeout);
                    await File.WriteAllTextAsync(fullPath, content ?? string.Empty, cts.Token);

                    context.ReadFiles.Add(fullPath);

                    result = new ToolResult($"File written: {filePath}", false);
                }
                catch (OperationCanceledException)
                {
                    result = new ToolResult($"Error: Timed out or cancelled writing file: {filePath}", false);
                }
                catch (Exception ex)
                {
                    result = new ToolResult($"Error: Failed to write file: {ex.Message}", false);
                }
            }
        }

        return result;
    }

	[Description("Performs an exact find-and-replace in a file. Locates oldContent in the file and replaces it with newContent.\n\nRules:\n- You must use read_file at least once before editing. This tool will error if you attempt an edit without reading the file first.\n- oldContent must appear exactly once in the file. The edit fails if it matches zero times or more than once.\n- Matching is byte-exact including all whitespace, indentation, and line endings.\n- If oldContent is not unique, include more surrounding lines of context to disambiguate.")]
	public static async Task<ToolResult> EditFileAsync(
		[Description("Absolute path to the file to modify.")] string filePath,
		[Description("The exact text to find in the file. Must match exactly once including all whitespace and indentation. Include surrounding context lines if needed to make the match unique.")] string oldContent,
		[Description("The text that replaces oldContent. Can be empty to delete the matched text.")] string newContent,
		[Description("Replace all occurrences of oldContent (default false)")] bool replaceAll,
		ToolContext context)
	{
		ToolResult result;
		CancellationToken cancellationToken = WorkerSession.CancellationToken;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult("Error: Path cannot be empty", false);
		}
		else if (string.IsNullOrEmpty(oldContent))
		{
			result = new ToolResult("Error: oldContent cannot be empty", false);
		}
		else if (!Path.IsPathRooted(filePath))
		{
			result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

			if (!context.ReadFiles.Contains(fullPath))
			{
				result = new ToolResult($"Error: You must use read_file on {filePath} before editing it.", false);
			}
			else
			{
				try
				{
					if (!File.Exists(fullPath))
					{
						result = new ToolResult($"Error: File not found: {filePath}", false);
					}
					else
					{
						using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						cts.CancelAfter(DefaultTimeout);
						string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);

						int firstIndex = fileContent.IndexOf(oldContent, StringComparison.Ordinal);
						if (firstIndex < 0)
						{
							result = new ToolResult($"Error: oldContent not found in file: {filePath}", false);
						}
						else
						{
							int secondIndex = fileContent.IndexOf(oldContent, firstIndex + oldContent.Length, StringComparison.Ordinal);
							if (secondIndex >= 0)
							{
								result = new ToolResult("Error: oldContent matched multiple times in file. Include more context to make it unique.", false);
							}
							else
							{
								string updatedContent = fileContent[..firstIndex] + (newContent ?? string.Empty) + fileContent[(firstIndex + oldContent.Length)..];
								await File.WriteAllTextAsync(fullPath, updatedContent, cts.Token);
								result = new ToolResult($"File edited: {filePath}", false);
							}
						}
					}
				}
				catch (OperationCanceledException)
				{
					result = new ToolResult($"Error: Timed out or cancelled editing file: {filePath}", false);
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: Failed to edit file: {ex.Message}", false);
				}
			}
		}

		return result;
	}

	[Description("""
		Performs multiple find-and-replace edits on a single file in one atomic operation.
		All edits are applied in sequence, each operating on the result of the previous edit.
		If any edit fails (oldContent not found or matches multiple times), none of the edits are applied.
		You must use read_file at least once before editing. This tool will error if you attempt edits without reading the file first.
		Prefer this tool over edit_file when you need to make multiple edits to the same file.
		""")]
	public static async Task<ToolResult> MultiEditFileAsync(
		[Description("Absolute path to the file to modify.")] string filePath,
		[Description("Array of edit operations to apply sequentially. Each edit has: oldContent (exact text to find), newContent (replacement text), and optionally replaceAll (boolean, default false).")] JsonArray edits,
		ToolContext context)
	{
		ToolResult result;
		CancellationToken cancellationToken = WorkerSession.CancellationToken;

		if (string.IsNullOrWhiteSpace(filePath))
		{
			result = new ToolResult("Error: Path cannot be empty", false);
		}
		else if (!Path.IsPathRooted(filePath))
		{
			result = new ToolResult($"Error: Path must be absolute: {filePath}", false);
		}
		else if (edits == null || edits.Count == 0)
		{
			result = new ToolResult("Error: edits array cannot be empty", false);
		}
		else
		{
			string fullPath = Path.GetFullPath(filePath);

			if (!context.ReadFiles.Contains(fullPath))
			{
				result = new ToolResult($"Error: You must use read_file on {filePath} before editing it.", false);
			}
			else
			{
				try
				{
					if (!File.Exists(fullPath))
					{
						result = new ToolResult($"Error: File not found: {filePath}", false);
					}
					else
					{
						using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
						cts.CancelAfter(DefaultTimeout);
						string fileContent = await File.ReadAllTextAsync(fullPath, cts.Token);

						string workingContent = fileContent;
						int editIndex = 0;

						foreach (JsonNode? editNode in edits)
						{
							editIndex++;

							if (editNode is not JsonObject editObj)
							{
								result = new ToolResult($"Error: Edit {editIndex} is not a valid object.", false);
								return result;
							}

							string? oldContent = editObj["oldContent"]?.ToString() ?? editObj["old_content"]?.ToString();
							string? newContent = editObj["newContent"]?.ToString() ?? editObj["new_content"]?.ToString();
							bool replaceAll = editObj["replaceAll"]?.GetValue<bool>() ?? editObj["replace_all"]?.GetValue<bool>() ?? false;

							if (string.IsNullOrEmpty(oldContent))
							{
								result = new ToolResult($"Error: Edit {editIndex}: oldContent cannot be empty.", false);
								return result;
							}

							int firstIndex = workingContent.IndexOf(oldContent, StringComparison.Ordinal);
							if (firstIndex < 0)
							{
								result = new ToolResult($"Error: Edit {editIndex}: oldContent not found in file. No edits were applied.", false);
								return result;
							}

							if (replaceAll)
							{
								workingContent = workingContent.Replace(oldContent, newContent ?? string.Empty, StringComparison.Ordinal);
							}
							else
							{
								int secondIndex = workingContent.IndexOf(oldContent, firstIndex + oldContent.Length, StringComparison.Ordinal);
								if (secondIndex >= 0)
								{
									result = new ToolResult($"Error: Edit {editIndex}: oldContent matched multiple times. Include more context to make it unique. No edits were applied.", false);
									return result;
								}

								workingContent = workingContent[..firstIndex] + (newContent ?? string.Empty) + workingContent[(firstIndex + oldContent.Length)..];
							}
						}

						await File.WriteAllTextAsync(fullPath, workingContent, cts.Token);
						result = new ToolResult($"File edited: {filePath} ({edits.Count} edit(s) applied)", false);
					}
				}
				catch (OperationCanceledException)
				{
					result = new ToolResult($"Error: Timed out or cancelled editing file: {filePath}", false);
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: Failed to edit file: {ex.Message}", false);
				}
			}
		}

		return result;
	}
}
