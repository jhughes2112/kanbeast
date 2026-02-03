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
    string LogDirectory { get; set; }
    string LogPrefix { get; set; }
}

// Wraps a single LLM endpoint and executes chat completions.
public class LlmService : ILlmService
{
    private readonly LLMConfig _config;
    private readonly List<string> _contextStatements;

    public string LogDirectory { get; set; } = string.Empty;
    public string LogPrefix { get; set; } = string.Empty;

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
            // Custom endpoint (OpenRouter, Azure, or other OpenAI-compatible APIs)
            // Use HttpClient with custom base address for non-Azure endpoints
            Uri endpoint = new Uri(_config.Endpoint);
            HttpClient httpClient = new HttpClient { BaseAddress = endpoint };
            builder.AddOpenAIChatCompletion(_config.Model, _config.ApiKey, httpClient: httpClient);
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

        string logPath = string.Empty;
        if (!string.IsNullOrEmpty(LogDirectory))
        {
            Directory.CreateDirectory(LogDirectory);
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            string filename = !string.IsNullOrEmpty(LogPrefix)
                ? $"{LogPrefix}-{timestamp}.txt"
                : $"request-{timestamp}.txt";
            logPath = Path.Combine(LogDirectory, filename);

            System.Text.StringBuilder requestLog = new System.Text.StringBuilder();
            requestLog.AppendLine("=== LLM REQUEST ===");
            requestLog.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            requestLog.AppendLine($"Model: {_config.Model}");
            requestLog.AppendLine();
            requestLog.AppendLine("=== SYSTEM PROMPT ===");
            requestLog.AppendLine(systemPrompt);
            requestLog.AppendLine();
            requestLog.AppendLine("=== USER PROMPT ===");
            requestLog.AppendLine(userPrompt);
            requestLog.AppendLine();

            await File.WriteAllTextAsync(logPath, requestLog.ToString(), cancellationToken);
        }

        ChatMessageContent response = await chat.GetChatMessageContentAsync(history, settings, kernel, cancellationToken);
        string content = response.Content ?? string.Empty;

        if (!string.IsNullOrEmpty(logPath))
        {
            System.Text.StringBuilder responseLog = new System.Text.StringBuilder();
            responseLog.AppendLine("=== LLM RESPONSE ===");
            responseLog.AppendLine($"Timestamp: {DateTime.UtcNow:O}");
            responseLog.AppendLine();
            responseLog.AppendLine(content);

            await File.AppendAllTextAsync(logPath, responseLog.ToString(), cancellationToken);
        }

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
