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
    public Task<string> ReadFileAsync(string filePath)
    {
        return _toolExecutor.ReadFileAsync(filePath);
    }

    [KernelFunction("write_file")]
    public Task WriteFileAsync(string filePath, string content)
    {
        return _toolExecutor.WriteFileAsync(filePath, content);
    }

    [KernelFunction("edit_file")]
    [Description("Replaces a single exact block of text. Fails if the block is missing or appears more than once.")]
    public Task EditFileAsync(string filePath, string oldContent, string newContent)
    {
        return _toolExecutor.EditFileAsync(filePath, oldContent, newContent);
    }

    [KernelFunction("patch_file")]
    [Description("Applies a unified diff patch using git apply from the specified working directory.")]
    public Task<string> PatchFileAsync(string workDir, string patch)
    {
        return _toolExecutor.PatchFileAsync(workDir, patch);
    }

    [KernelFunction("list_files")]
    public Task<string> ListFilesAsync(string directoryPath)
    {
        return _toolExecutor.ListFilesAsync(directoryPath);
    }
}
