using KanBeast.Worker.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace KanBeast.Worker.Services;

// Defines the LLM operations required by the worker agents.
public interface ILlmService
{
    Kernel CreateKernel(IEnumerable<object> tools);
    Task<string> RunAsync(Kernel kernel, string systemPrompt, string userPrompt, CancellationToken cancellationToken);
    Task AddContextStatementAsync(string statement, CancellationToken cancellationToken);
    Task ClearContextStatementsAsync(CancellationToken cancellationToken);
    IReadOnlyList<string> GetContextStatements();
}

// Wraps a single LLM endpoint and executes chat completions.
public class LlmService : ILlmService
{
    private readonly LLMConfig _config;
    private readonly List<string> _contextStatements;

    public LlmService(LLMConfig config)
    {
        _config = config;
        _contextStatements = new List<string>();
    }

    public Kernel CreateKernel(IEnumerable<object> tools)
    {
        IKernelBuilder builder = Kernel.CreateBuilder();

        if (!string.IsNullOrWhiteSpace(_config.Endpoint))
        {
            builder.AddAzureOpenAIChatCompletion(_config.Model, _config.Endpoint, _config.ApiKey);
        }
        else
        {
            builder.AddOpenAIChatCompletion(_config.Model, _config.ApiKey);
        }

        Kernel kernel = builder.Build();
        foreach (object tool in tools)
        {
            kernel.Plugins.AddFromObject(tool);
        }

        return kernel;
    }

    public async Task<string> RunAsync(Kernel kernel, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        IChatCompletionService chat = kernel.GetRequiredService<IChatCompletionService>();
        ChatHistory history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        OpenAIPromptExecutionSettings settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        };

        ChatMessageContent response = await chat.GetChatMessageContentAsync(history, settings, kernel, cancellationToken);
        string content = response.Content ?? string.Empty;

        return content;
    }

    public Task AddContextStatementAsync(string statement, CancellationToken cancellationToken)
    {
        _contextStatements.Add(statement);

        return Task.CompletedTask;
    }

    public Task ClearContextStatementsAsync(CancellationToken cancellationToken)
    {
        _contextStatements.Clear();

        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetContextStatements()
    {
        IReadOnlyList<string> statements = _contextStatements.AsReadOnly();

        return statements;
    }
}
