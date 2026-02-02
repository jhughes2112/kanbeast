using KanBeast.Worker.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;

namespace KanBeast.Worker.Services;

public interface ILlmService
{
    Kernel CreateKernel(IEnumerable<object> tools);
    Task<string> RunAsync(Kernel kernel, string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

public class LlmService : ILlmService
{
    private readonly LLMConfig _config;

    public LlmService(IEnumerable<LLMConfig> configs)
    {
        _config = SelectConfig(configs);
    }

    public Kernel CreateKernel(IEnumerable<object> tools)
    {
        var builder = Kernel.CreateBuilder();

        if (!string.IsNullOrWhiteSpace(_config.Endpoint))
        {
            builder.Services.AddSingleton<IChatCompletionService>(
                new OpenAIChatCompletionService(_config.Model, _config.ApiKey, endpoint: new Uri(_config.Endpoint)));
        }
        else
        {
            builder.AddOpenAIChatCompletion(_config.Model, _config.ApiKey);
        }

        var kernel = builder.Build();
        foreach (var tool in tools)
        {
            kernel.Plugins.AddFromObject(tool);
        }

        return kernel;
    }

    public async Task<string> RunAsync(Kernel kernel, string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();
        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage(userPrompt);

        var settings = new OpenAIPromptExecutionSettings
        {
            ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
        };

        var response = await chat.GetChatMessageContentAsync(history, settings, kernel, cancellationToken);
        return response.Content ?? string.Empty;
    }

    private static LLMConfig SelectConfig(IEnumerable<LLMConfig> configs)
    {
        var config = configs
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.Priority)
            .FirstOrDefault();

        if (config == null)
        {
            throw new InvalidOperationException("No enabled LLM configuration is available.");
        }

        return config;
    }
}
