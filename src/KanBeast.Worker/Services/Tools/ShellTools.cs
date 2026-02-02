using Microsoft.SemanticKernel;

namespace KanBeast.Worker.Services.Tools;

public class ShellTools
{
    private readonly IToolExecutor _toolExecutor;

    public ShellTools(IToolExecutor toolExecutor)
    {
        _toolExecutor = toolExecutor;
    }

    [KernelFunction("execute_bash")]
    public Task<string> ExecuteBashAsync(string command, string workDir)
    {
        return _toolExecutor.ExecuteBashCommandAsync(command, workDir);
    }
}
