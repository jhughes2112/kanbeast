using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KanBeast.Worker.Models;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

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

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }

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

public class UsageInfo
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }

    [JsonPropertyName("cost")]
    public decimal? Cost { get; set; }
}

public class LlmResult
{
    public string Content { get; set; } = string.Empty;
    public decimal AccumulatedCost { get; set; }
}

// Direct OpenAI-compatible chat completion client with tool calling.
public class LlmService
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private readonly LLMConfig _config;
	private readonly HttpClient _httpClient;
	private readonly ICompaction _compaction;
	private readonly string _logDirectory;
	private readonly string _logPrefix;

	public LlmService(LLMConfig config, ICompaction compaction, string logDirectory, string logPrefix)
	{
		_config = config;
		_compaction = compaction;
		_logDirectory = logDirectory;
		_logPrefix = logPrefix;

		if (string.IsNullOrWhiteSpace(config.Endpoint))
		{
			throw new InvalidOperationException("LLM endpoint is required, such as https://api.openai.com/v1");
		}

		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
	}

	public async Task<LlmResult> RunAsync(LlmConversation conversation, IEnumerable<IToolProvider> providers, LlmRole role, CancellationToken cancellationToken)
	{
		List<Tool> tools = new List<Tool>();

		foreach (IToolProvider provider in providers)
		{
			provider.AddTools(tools, role);
		}

		List<ToolDefinition>? toolDefs = null;

		if (tools.Count > 0)
		{
			toolDefs = new List<ToolDefinition>();
			foreach (Tool tool in tools)
			{
				toolDefs.Add(tool.Definition);
			}
		}

		string finalContent = string.Empty;
		decimal accumulatedCost = 0m;
		int maxIterations = 50;
		int iteration = 0;

        while (iteration < maxIterations)
        {
            iteration++;
            cancellationToken.ThrowIfCancellationRequested();

            decimal compactionCost = await _compaction.CompactAsync(conversation, this, _logDirectory, _logPrefix, cancellationToken);
            accumulatedCost += compactionCost;

            ChatCompletionRequest request = new ChatCompletionRequest
            {
                Model = _config.Model,
                Messages = conversation.Messages,
                Tools = toolDefs,
                ToolChoice = toolDefs != null ? "auto" : null
            };

            string requestJson = JsonSerializer.Serialize(request, JsonOptions);
            string fullUrl = $"{_config.Endpoint!.TrimEnd('/')}/chat/completions";
            Console.WriteLine($"LLM Request to {fullUrl}:");
            Console.WriteLine(requestJson);

            StringContent content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
            HttpResponseMessage httpResponse = await _httpClient.PostAsync(
                fullUrl,
                content,
                cancellationToken);

            string responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
            Console.WriteLine($"LLM Response ({httpResponse.StatusCode}):");
            Console.WriteLine(responseBody);

            if (!httpResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"API error {httpResponse.StatusCode}: {responseBody}");
            }

            ChatCompletionResponse? response = null;

            try
            {
                response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON response: {ex.Message}. Body: {responseBody}");
            }

            if (response == null || response.Choices.Count == 0)
            {
                throw new InvalidOperationException($"Empty response from API: {responseBody}");
            }

            if (response.Error != null)
            {
                throw new InvalidOperationException($"API error: {response.Error.Message}");
            }

            if (response.Usage != null)
            {
                if (response.Usage.Cost.HasValue)
                {
                    accumulatedCost += response.Usage.Cost.Value;
                }
                else
                {
                    decimal inputCost = response.Usage.PromptTokens * _config.InputTokenPrice;
                    decimal outputCost = response.Usage.CompletionTokens * _config.OutputTokenPrice;
                    accumulatedCost += inputCost + outputCost;
                }
            }

            ChatChoice choice = response.Choices[0];
            ChatMessage assistantMessage = choice.Message;
            conversation.AddAssistantMessage(assistantMessage);

            if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
            {
                finalContent = assistantMessage.Content ?? string.Empty;
                break;
            }

            foreach (ToolCallMessage toolCall in assistantMessage.ToolCalls)
            {
                string toolResult = $"Error: Unknown tool '{toolCall.Function.Name}'";

                foreach (Tool tool in tools)
                {
                    if (tool.Definition.Function.Name == toolCall.Function.Name)
                    {
                        JsonObject args;
                        try
                        {
                            args = JsonNode.Parse(toolCall.Function.Arguments)?.AsObject() ?? new JsonObject();
                        }
                        catch
                        {
                            args = new JsonObject();
                        }

                        try
                        {
                            toolResult = await tool.Handler(args);
                        }
                        catch (Exception ex)
                        {
                            toolResult = $"Error: {ex.Message}";
                        }
                        break;
                    }
                }

                conversation.AddToolMessage(toolCall.Id, toolResult);
            }
        }

        return new LlmResult
        {
            Content = finalContent,
            AccumulatedCost = accumulatedCost
        };
    }
}
