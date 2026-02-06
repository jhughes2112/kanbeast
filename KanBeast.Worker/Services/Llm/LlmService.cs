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
	public bool Success { get; set; } = true;
	public string Content { get; set; } = string.Empty;
	public string ErrorMessage { get; set; } = string.Empty;
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
	private readonly bool _jsonLogging;

	private int _backoffSeconds = 0;

	public LlmService(LLMConfig config, bool jsonLogging)
	{
		_config = config;
		_jsonLogging = jsonLogging;

		if (string.IsNullOrWhiteSpace(config.Endpoint))
		{
			throw new InvalidOperationException("LLM endpoint is required, such as https://api.openai.com/v1");
		}

		_httpClient = new HttpClient();
		_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
	}

	public async Task<LlmResult> RunAsync(LlmConversation conversation, IEnumerable<IToolProvider> providers, LlmRole role, ICompaction? compaction, CancellationToken cancellationToken)
	{
		List<Tool> tools = new List<Tool>();

		foreach (IToolProvider provider in providers)
		{
			provider.AddTools(tools, role);
		}

		List<ToolDefinition>? toolDefs = tools.Count > 0 ? new List<ToolDefinition>() : null;

		foreach (Tool tool in tools)
		{
			toolDefs?.Add(tool.Definition);
		}

		string finalContent = string.Empty;
		string errorMessage = string.Empty;
		bool success = false;
		decimal accumulatedCost = 0m;
		int maxIterations = 50;
		int iteration = 0;
		int networkRetries = 0;
		int maxNetworkRetries = 10;

		while (iteration < maxIterations && !success && string.IsNullOrEmpty(errorMessage))
		{
			iteration++;
			cancellationToken.ThrowIfCancellationRequested();

			if (compaction != null)
			{
				decimal compactionCost = await compaction.CompactAsync(conversation, this, _jsonLogging, cancellationToken);
				accumulatedCost += compactionCost;
			}

			ChatCompletionRequest request = new ChatCompletionRequest
			{
				Model = _config.Model,
				Messages = conversation.Messages,
				Tools = toolDefs,
				ToolChoice = toolDefs != null ? "auto" : null
			};

			string requestJson = JsonSerializer.Serialize(request, JsonOptions);
			string fullUrl = $"{_config.Endpoint!.TrimEnd('/')}/chat/completions";

			await WaitForRateLimitAsync(cancellationToken);

			HttpResponseMessage? httpResponse = null;
			string responseBody = string.Empty;
			bool shouldRetry = false;

			try
			{
				StringContent content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
				httpResponse = await _httpClient.PostAsync(fullUrl, content, cancellationToken);
				responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);
			}
			catch (HttpRequestException ex)
			{
				networkRetries++;
				if (networkRetries >= maxNetworkRetries)
				{
					errorMessage = $"Network error after {networkRetries} attempts: {ex.Message}";
				}
				else
				{
					_backoffSeconds = _backoffSeconds + 3;
					Console.WriteLine($"Network error (attempt {networkRetries}/{maxNetworkRetries}): {ex.Message}. Waiting {_backoffSeconds}s...");
					iteration--;
					shouldRetry = true;
				}
			}
			catch (TaskCanceledException ex)
			{
				networkRetries++;
				if (networkRetries >= maxNetworkRetries)
				{
					errorMessage = $"Request timeout after {networkRetries} attempts: {ex.Message}";
				}
				else
				{
					_backoffSeconds = _backoffSeconds + 3;
					Console.WriteLine($"Request timeout (attempt {networkRetries}/{maxNetworkRetries}). Waiting {_backoffSeconds}s...");
					iteration--;
					shouldRetry = true;
				}
			}

			if (!shouldRetry && string.IsNullOrEmpty(errorMessage) && httpResponse != null)
			{
				networkRetries = 0;

				if (IsRateLimited(httpResponse, responseBody))
				{
					int waitSeconds = ParseRateLimitSeconds(httpResponse, responseBody);
					_backoffSeconds = waitSeconds > 0 ? waitSeconds : _backoffSeconds + 3;
					Console.WriteLine($"Rate limited. Waiting {_backoffSeconds}s before retry...");
					iteration--;
				}
				else if ((int)httpResponse.StatusCode >= 500 && (int)httpResponse.StatusCode < 600)
				{
					_backoffSeconds = _backoffSeconds + 3;
					Console.WriteLine($"Server error {httpResponse.StatusCode}. Waiting {_backoffSeconds}s before retry...");
					iteration--;
				}
				else if (httpResponse.IsSuccessStatusCode)
				{
					_backoffSeconds = 0;
					ChatCompletionResponse? response = null;

					try
					{
						response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);
					}
					catch (JsonException ex)
					{
						errorMessage = $"Invalid JSON response: {ex.Message}";
					}

					if (response != null && response.Choices.Count > 0 && response.Error == null)
					{
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
							success = true;
						}
						else
						{
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
					}
					else if (response == null || response.Choices.Count == 0)
					{
						errorMessage = "Empty response from API";
					}
					else if (response.Error != null)
					{
						errorMessage = $"API error: {response.Error.Message}";
					}
				}
				else
				{
					errorMessage = $"API error {httpResponse.StatusCode}: {responseBody}";
				}
			}
		}

		return new LlmResult
		{
			Success = success || string.IsNullOrEmpty(errorMessage),
			Content = finalContent,
			ErrorMessage = errorMessage,
			AccumulatedCost = accumulatedCost
		};
	}

	private async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
	{
		if (_backoffSeconds > 0)
		{
			int maxBackoffSeconds = 60;
			_backoffSeconds = Math.Min(_backoffSeconds, maxBackoffSeconds);
			Console.WriteLine($"Rate limit backoff: waiting {_backoffSeconds}s");
			await Task.Delay(TimeSpan.FromSeconds(_backoffSeconds), cancellationToken);
		}
	}

	private bool IsRateLimited(HttpResponseMessage response, string responseBody)
	{
		return response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
			responseBody.Contains("\"code\":429") ||
			GetFirstHeaderValue(response.Headers, "Retry-After") != null ||
			(GetFirstHeaderValue(response.Headers, "X-RateLimit-Remaining") is string remaining && int.TryParse(remaining, out int count) && count == 0);
	}

	private int ParseRateLimitSeconds(HttpResponseMessage response, string responseBody)
	{
		string? retryAfter = GetFirstHeaderValue(response.Headers, "Retry-After");
		if (retryAfter != null && int.TryParse(retryAfter, out int seconds))
		{
			return seconds + 1;
		}

		string? rateLimitReset = GetFirstHeaderValue(response.Headers, "X-RateLimit-Reset");
		if (rateLimitReset != null && long.TryParse(rateLimitReset, out long epochValue))
		{
			return EpochToSecondsFromNow(epochValue);
		}

		if (responseBody.Contains("X-RateLimit-Reset"))
		{
			int parsed = ParseRateLimitSecondsFromErrorBody(responseBody);
			if (parsed > 0)
			{
				return parsed;
			}
		}

		return 0;
	}

	private static string? GetFirstHeaderValue(System.Net.Http.Headers.HttpResponseHeaders headers, string headerName)
	{
		if (headers.TryGetValues(headerName, out IEnumerable<string>? values))
		{
			foreach (string value in values)
			{
				return value;
			}
		}

		return null;
	}

	private int ParseRateLimitSecondsFromErrorBody(string responseBody)
	{
		try
		{
			JsonDocument doc = JsonDocument.Parse(responseBody);

			if (doc.RootElement.TryGetProperty("error", out JsonElement errorElement) &&
				errorElement.TryGetProperty("metadata", out JsonElement metadataElement) &&
				metadataElement.TryGetProperty("headers", out JsonElement headersElement) &&
				headersElement.TryGetProperty("X-RateLimit-Reset", out JsonElement resetElement))
			{
				string? resetStr = resetElement.GetString();
				if (!string.IsNullOrEmpty(resetStr) && long.TryParse(resetStr, out long epochValue))
				{
					return EpochToSecondsFromNow(epochValue);
				}
			}
		}
		catch (JsonException)
		{
		}

		return 0;
	}

	private static int EpochToSecondsFromNow(long epochValue)
	{
		long epochSeconds = epochValue > 2_000_000_000 ? epochValue / 1000 : epochValue;
		long nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
		long delta = epochSeconds - nowSeconds + 1;
		return delta > 0 ? (int)delta : 0;
	}
}
