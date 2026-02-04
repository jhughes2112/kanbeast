using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KanBeast.Worker.Models;

namespace KanBeast.Worker.Services;

// A tool that can be invoked by the LLM.
public class LlmTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonObject Parameters { get; init; }
    public required Func<JsonObject, Task<string>> InvokeAsync { get; init; }
}

// A message in the chat conversation.
public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCallMessage>? ToolCalls { get; set; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public class ToolCallMessage
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionCallMessage Function { get; set; } = new();
}

public class FunctionCallMessage
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

// OpenAI API request/response structures.
public class ChatCompletionRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolDefinition>? Tools { get; set; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolChoice { get; set; }
}

public class ToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public FunctionDefinition Function { get; set; } = new();
}

public class FunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public JsonObject Parameters { get; set; } = new();
}

public class ChatCompletionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("choices")]
    public List<ChatChoice> Choices { get; set; } = new();

    [JsonPropertyName("error")]
    public ApiError? Error { get; set; }
}

public class ChatChoice
{
    [JsonPropertyName("message")]
    public ChatMessage Message { get; set; } = new();

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class ApiError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

// Session log containing the full conversation.
public class SessionLog
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("started_at")]
    public string StartedAt { get; set; } = string.Empty;

    [JsonPropertyName("completed_at")]
    public string CompletedAt { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = new();

    [JsonPropertyName("tool_invocations")]
    public List<ToolInvocationLog> ToolInvocations { get; set; } = new();
}

public class ToolInvocationLog
{
    [JsonPropertyName("tool_call_id")]
    public string ToolCallId { get; set; } = string.Empty;

    [JsonPropertyName("function_name")]
    public string FunctionName { get; set; } = string.Empty;

    [JsonPropertyName("arguments")]
    public JsonObject? Arguments { get; set; }

    [JsonPropertyName("result")]
    public string Result { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;
}

// Defines the LLM operations required by the worker agents.
public interface ILlmService
{
    void RegisterToolsFromProviders(IEnumerable<Tools.IToolProvider> providers, Tools.LlmRole role);
    Task<string> RunAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken);
    string LogDirectory { get; set; }
    string LogPrefix { get; set; }
}

// Direct OpenAI-compatible chat completion client with tool calling.
public class LlmService : ILlmService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly LLMConfig _config;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, LlmTool> _tools;

    public string LogDirectory { get; set; } = string.Empty;
    public string LogPrefix { get; set; } = string.Empty;

    public LlmService(LLMConfig config)
    {
        _config = config;
        _tools = new Dictionary<string, LlmTool>();

        string baseUrl = !string.IsNullOrWhiteSpace(config.Endpoint)
            ? config.Endpoint.TrimEnd('/')
            : "https://api.openai.com/v1";

        _httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
    }

    public void RegisterToolsFromProviders(IEnumerable<Tools.IToolProvider> providers, Tools.LlmRole role)
    {
        _tools.Clear();

        foreach (Tools.IToolProvider provider in providers)
        {
            foreach (KeyValuePair<string, Tools.ProviderTool> entry in provider.GetTools(role))
            {
                Tools.ProviderTool def = entry.Value;
                _tools[def.Name] = new LlmTool
                {
                    Name = def.Name,
                    Description = def.Description,
                    Parameters = def.Parameters,
                    InvokeAsync = def.InvokeAsync
                };
            }
        }
    }

    public async Task<string> RunAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        SessionLog session = new SessionLog
        {
            Model = _config.Model,
            StartedAt = DateTime.UtcNow.ToString("O")
        };

        List<ChatMessage> messages = new List<ChatMessage>
        {
            new ChatMessage { Role = "system", Content = systemPrompt },
            new ChatMessage { Role = "user", Content = userPrompt }
        };

        List<ToolDefinition>? toolDefs = null;
        if (_tools.Count > 0)
        {
            toolDefs = new List<ToolDefinition>();
            foreach (LlmTool tool in _tools.Values)
            {
                toolDefs.Add(new ToolDefinition
                {
                    Type = "function",
                    Function = new FunctionDefinition
                    {
                        Name = tool.Name,
                        Description = tool.Description,
                        Parameters = tool.Parameters
                    }
                });
            }
        }

        string finalContent = string.Empty;
        int maxIterations = 50;
        int iteration = 0;

        while (iteration < maxIterations)
        {
            iteration++;
            cancellationToken.ThrowIfCancellationRequested();

            ChatCompletionRequest request = new ChatCompletionRequest
            {
                Model = _config.Model,
                Messages = messages,
                Tools = toolDefs,
                ToolChoice = toolDefs != null ? "auto" : null
            };

            HttpResponseMessage httpResponse = await _httpClient.PostAsJsonAsync(
                "/chat/completions",
                request,
                JsonOptions,
                cancellationToken);

            string responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"API error {httpResponse.StatusCode}: {responseBody}");
            }

            ChatCompletionResponse? response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);

            if (response == null || response.Choices.Count == 0)
            {
                throw new InvalidOperationException($"Empty response from API: {responseBody}");
            }

            if (response.Error != null)
            {
                throw new InvalidOperationException($"API error: {response.Error.Message}");
            }

            ChatChoice choice = response.Choices[0];
            ChatMessage assistantMessage = choice.Message;
            messages.Add(assistantMessage);

            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                finalContent = assistantMessage.Content ?? string.Empty;
                break;
            }

            foreach (ToolCallMessage toolCall in assistantMessage.ToolCalls)
            {
                string toolResult;
                JsonObject? parsedArgs = null;

                try
                {
                    parsedArgs = JsonNode.Parse(toolCall.Function.Arguments)?.AsObject();
                }
                catch
                {
                    parsedArgs = new JsonObject();
                }

                if (_tools.TryGetValue(toolCall.Function.Name, out LlmTool? tool))
                {
                    try
                    {
                        toolResult = await tool.InvokeAsync(parsedArgs ?? new JsonObject());
                    }
                    catch (Exception ex)
                    {
                        toolResult = $"Error: {ex.Message}";
                    }
                }
                else
                {
                    toolResult = $"Error: Unknown tool '{toolCall.Function.Name}'";
                }

                session.ToolInvocations.Add(new ToolInvocationLog
                {
                    ToolCallId = toolCall.Id,
                    FunctionName = toolCall.Function.Name,
                    Arguments = parsedArgs,
                    Result = toolResult,
                    Timestamp = DateTime.UtcNow.ToString("O")
                });

                messages.Add(new ChatMessage
                {
                    Role = "tool",
                    Content = toolResult,
                    ToolCallId = toolCall.Id
                });
            }
        }

        session.CompletedAt = DateTime.UtcNow.ToString("O");
        session.Messages = messages;

        if (!string.IsNullOrEmpty(LogDirectory))
        {
            await WriteSessionLogAsync(session, cancellationToken);
        }

        return finalContent;
    }

    private async Task WriteSessionLogAsync(SessionLog session, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(LogDirectory);

        long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string filename = !string.IsNullOrEmpty(LogPrefix)
            ? $"{LogPrefix}-{timestamp}.json"
            : $"session-{timestamp}.json";
        string logPath = Path.Combine(LogDirectory, filename);

        int duplicateIndex = 1;
        while (File.Exists(logPath))
        {
            string duplicateName = !string.IsNullOrEmpty(LogPrefix)
                ? $"{LogPrefix}-{timestamp}-{duplicateIndex}.json"
                : $"session-{timestamp}-{duplicateIndex}.json";
            logPath = Path.Combine(LogDirectory, duplicateName);
            duplicateIndex++;
        }

        string json = JsonSerializer.Serialize(session, JsonOptions);
        await File.WriteAllTextAsync(logPath, json, cancellationToken);
    }
}
