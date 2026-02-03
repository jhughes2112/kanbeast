using System.ComponentModel;
using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

public class FileTools
{
    private readonly IToolExecutor _toolExecutor;

    public FileTools(IToolExecutor toolExecutor)
    {
        _toolExecutor = toolExecutor;
    }

    [KernelFunction("read_file")]
    [Description("Read the entire contents of a file. For large files, prefer read_file_lines to read specific sections.")]
    public Task<string> ReadFileAsync(string filePath)
    {
        return _toolExecutor.ReadFileAsync(filePath);
    }

    [KernelFunction("read_file_lines")]
    [Description("Read specific line ranges from a file with line number prefixes (e.g., '42: code'). Use this to read targeted sections of large files. Lines are 1-based.")]
    public Task<string> ReadFileLinesAsync(string filePath, int startLine, int endLine)
    {
        return _toolExecutor.ReadFileLinesAsync(filePath, startLine, endLine);
    }

    [KernelFunction("write_file")]
    [Description("Write content to a file, creating or overwriting as needed. The directory will be created if it does not exist.")]
    public Task WriteFileAsync(string filePath, string content)
    {
        return _toolExecutor.WriteFileAsync(filePath, content);
    }

    [KernelFunction("create_file")]
    [Description("Create a new file with the specified content. Fails if the file already exists. Use write_file to overwrite existing files.")]
    public Task CreateFileAsync(string filePath, string content)
    {
        return _toolExecutor.CreateFileAsync(filePath, content);
    }

    [KernelFunction("edit_file")]
    [Description("Replace a single exact block of text in a file. The oldContent must match exactly and appear only once. Include surrounding context (3-5 lines) to ensure uniqueness. Fails if the block is missing or appears more than once.")]
    public Task EditFileAsync(string filePath, string oldContent, string newContent)
    {
        return _toolExecutor.EditFileAsync(filePath, oldContent, newContent);
    }

    [KernelFunction("multi_edit_file")]
    [Description("Apply multiple text replacements to a single file in one operation. More efficient than multiple edit_file calls. Pass editsJson as a JSON array of objects with 'oldContent' and 'newContent' properties. Each oldContent must match exactly once. Example: [{\"oldContent\": \"old1\", \"newContent\": \"new1\"}, {\"oldContent\": \"old2\", \"newContent\": \"new2\"}]")]
    public Task<string> MultiEditFileAsync(string filePath, string editsJson)
    {
        return _toolExecutor.MultiEditFileAsync(filePath, editsJson);
    }

    [KernelFunction("list_files")]
    [Description("List all files and directories in the specified directory path. Returns one entry per line.")]
    public Task<string> ListFilesAsync(string directoryPath)
    {
        return _toolExecutor.ListFilesAsync(directoryPath);
    }

    [KernelFunction("search_files")]
    [Description("Search for files in a directory by name or relative path pattern. Returns matching file paths relative to the search directory. Limited to maxResults (default 50).")]
    public Task<string> SearchFilesAsync(string directoryPath, string searchPattern, int maxResults)
    {
        return _toolExecutor.SearchFilesAsync(directoryPath, searchPattern, maxResults);
    }

    [KernelFunction("remove_file")]
    [Description("Delete a file from the filesystem. Returns true if the file was deleted, false if it did not exist.")]
    public Task<bool> RemoveFileAsync(string filePath)
    {
        return _toolExecutor.RemoveFileAsync(filePath);
    }

    [KernelFunction("file_exists")]
    [Description("Check if a file exists at the specified path. Returns true if the file exists, false otherwise.")]
    public Task<bool> FileExistsAsync(string filePath)
    {
        return _toolExecutor.FileExistsAsync(filePath);
    }

    [KernelFunction("directory_exists")]
    [Description("Check if a directory exists at the specified path. Returns true if the directory exists, false otherwise.")]
    public Task<bool> DirectoryExistsAsync(string directoryPath)
    {
        return _toolExecutor.DirectoryExistsAsync(directoryPath);
    }
}
