using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KanBeast.Shared;
using KanBeast.Worker.Services.Tools;

namespace KanBeast.Worker.Services;

// OpenAI API request/response structures.
public class ChatCompletionRequest
{
	[JsonPropertyName("model")]
	public string Model { get; set; } = string.Empty;

	[JsonPropertyName("messages")]
	public List<ConversationMessage> Messages { get; set; } = new();

	[JsonPropertyName("tools")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public List<ToolDefinition>? Tools { get; set; }

	[JsonPropertyName("tool_choice")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? ToolChoice { get; set; }

	[JsonPropertyName("parallel_tool_calls")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public bool? ParallelToolCalls { get; set; }

	[JsonPropertyName("temperature")]
	public double Temperature { get; set; }

	[JsonPropertyName("top_p")]
	public double TopP { get; set; }

	[JsonPropertyName("frequency_penalty")]
	public double FrequencyPenalty { get; set; }

	[JsonPropertyName("seed")]
	public int Seed { get; set; }

	[JsonPropertyName("max_completion_tokens")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? MaxCompletionTokens { get; set; }

	[JsonPropertyName("max_tokens")]
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public int? MaxTokens { get; set; }
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
	public ConversationMessage Message { get; set; } = new();

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

public enum LlmExitReason
{
	Completed,             // LLM finished with a content response (no tool calls)
	ToolRequestedExit,     // A tool set ExitLoop = true
	LlmCallFailed,         // Network error, API error, or invalid response
	MaxIterationsReached,  // Hit iteration limit without completion
	CostExceeded,          // Accumulated cost exceeded the budget
	RateLimited            // Rate limited, RetryAfter indicates when to retry
}

public class LlmResult
{
	public LlmExitReason ExitReason { get; set; } = LlmExitReason.Completed;
	public bool Success => ExitReason == LlmExitReason.Completed || ExitReason == LlmExitReason.ToolRequestedExit;
	public string Content { get; set; } = string.Empty;
	public string ErrorMessage { get; set; } = string.Empty;
	public string? FinalToolCalled { get; set; }
	public DateTimeOffset? RetryAfter { get; set; }
}

// Direct OpenAI-compatible chat completion client with tool calling.
public class LlmService
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	private static readonly TimeSpan DownCooldown = TimeSpan.FromMinutes(5);

	private readonly Random SeedRandom = new();

	private readonly LLMConfig _config;
	private readonly HttpClient _httpClient;

	private bool _parallelToolCallsSupported = true;
	private bool _hasSucceeded;
	private DateTimeOffset _availableAt = DateTimeOffset.MinValue;
	private bool _isPermanentlyDown;

	public string Model => _config.Model;
	public int ContextLength => _config.ContextLength;
	public LLMConfig Config => _config;
	public bool IsAvailable => !_isPermanentlyDown && DateTimeOffset.UtcNow >= _availableAt;
	public bool IsPermanentlyDown => _isPermanentlyDown;
	public DateTimeOffset AvailableAt => _availableAt;

	public LlmService(LLMConfig config)
	{
		_config = config;

		if (string.IsNullOrWhiteSpace(config.Endpoint))
		{
			throw new InvalidOperationException("LLM endpoint is required, such as https://api.openai.com/v1");
		}

		_httpClient = new HttpClient();
		_httpClient.Timeout = TimeSpan.FromMinutes(5);  // ridiculously long timeout, because sometimes openrouter takes its time
		_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {config.ApiKey}");
	}

	public async Task<(LlmResult result, decimal cost)> RunAsync(LlmConversation conversation, decimal remainingBudget, int? maxCompletionTokens, CancellationToken cancellationToken)
	{
		List<Tool> tools = ToolsFactory.GetTools(conversation.Role);
		List<ToolDefinition>? toolDefs = tools.Count > 0 ? new List<ToolDefinition>() : null;
		foreach (Tool tool in tools)
		{
			toolDefs?.Add(tool.Definition);
		}

		decimal accumulatedCost = 0m;
		int transientRetries = 0;
		int maxTransientRetries = _hasSucceeded ? 3 : 1;

		for (; ; )
		{
			if (conversation.HasReachedMaxIterations)
			{
				return (new LlmResult { ExitReason = LlmExitReason.MaxIterationsReached }, accumulatedCost);
			}

			if (remainingBudget > 0 && accumulatedCost >= remainingBudget)
			{
				return (new LlmResult { ExitReason = LlmExitReason.CostExceeded }, accumulatedCost);
			}

			cancellationToken.ThrowIfCancellationRequested();

			// Inject any pending user messages from the chat hub.
			ConcurrentQueue<string> chatQueue = WorkerSession.GetChatQueue(conversation.Id);
			while (chatQueue.TryDequeue(out string? chatMsg))
			{
				await conversation.AddUserMessageAsync(chatMsg, cancellationToken);
			}

			// Some providers (e.g. Anthropic) reject conversations that don't end with a user or tool message.
			// Build the message list for the request, appending a kickoff if the last message is not user/tool.
			List<ConversationMessage> requestMessages = conversation.Messages;
			if (requestMessages.Count > 0)
			{
				string lastRole = requestMessages[requestMessages.Count - 1].Role;
				if (lastRole != "user" && lastRole != "tool")
				{
					requestMessages = new List<ConversationMessage>(requestMessages);
					requestMessages.Add(new ConversationMessage { Role = "user", Content = "Continue." });
				}
			}

			ChatCompletionRequest request = new ChatCompletionRequest
			{
				Model = _config.Model,
				Messages = requestMessages,
				Tools = toolDefs,
				ParallelToolCalls = toolDefs != null && _parallelToolCallsSupported ? true : null,
				Temperature = _config.Temperature,
				TopP = 1.0,
				FrequencyPenalty = 0.1,
				Seed = SeedRandom.Next(),
				MaxCompletionTokens = maxCompletionTokens,
				MaxTokens = maxCompletionTokens
			};

			string requestJson = JsonSerializer.Serialize(request, JsonOptions);
			string fullUrl = $"{_config.Endpoint!.TrimEnd('/')}/chat/completions";

			try
			{
				StringContent content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
				HttpResponseMessage httpResponse = await _httpClient.PostAsync(fullUrl, content, cancellationToken);
				string responseBody = await httpResponse.Content.ReadAsStringAsync(cancellationToken);

				if (!httpResponse.IsSuccessStatusCode)
				{
					Console.WriteLine($"[LLM ERROR] {_config.Model} returned {(int)httpResponse.StatusCode} {httpResponse.StatusCode}");
					Console.WriteLine($"[LLM ERROR] Response body: {responseBody}");
				}

				if (httpResponse.IsSuccessStatusCode)
				{
					_hasSucceeded = true;
					transientRetries = 0;

					ChatCompletionResponse? response = JsonSerializer.Deserialize<ChatCompletionResponse>(responseBody, JsonOptions);
					if (response != null && response.Choices.Count > 0 && response.Error == null)
					{
						conversation.IncrementIteration();

						if (response.Usage != null)
						{
							if (response.Usage.Cost.HasValue)
							{
								accumulatedCost += response.Usage.Cost.Value;
							}
							else
							{
								accumulatedCost += (response.Usage.PromptTokens / 1_000_000m) * _config.InputTokenPrice;
								accumulatedCost += (response.Usage.CompletionTokens / 1_000_000m) * _config.OutputTokenPrice;
							}
						}

						ConversationMessage assistantMessage = response.Choices[0].Message;

						// If no native tool calls, try parsing XML-style tool calls from content.
						if ((assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0) && !string.IsNullOrEmpty(assistantMessage.Content))
						{
							List<ConversationToolCall>? xmlToolCalls = TryParseXmlToolCalls(assistantMessage.Content, tools);
							if (xmlToolCalls != null)
							{
								assistantMessage.ToolCalls = xmlToolCalls;
								Console.WriteLine($"Parsed {xmlToolCalls.Count} XML-style tool call(s) from {_config.Model} response");
							}
						}

						await conversation.AddAssistantMessageAsync(assistantMessage, _config.Model, cancellationToken);

						if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
						{
							return (new LlmResult
							{
								ExitReason = LlmExitReason.Completed,
								Content = assistantMessage.Content ?? string.Empty
							}, accumulatedCost);
						}

						// Start all tool calls concurrently.
						List<(ConversationToolCall Call, Task<ToolResult> Task)> pendingTools = new List<(ConversationToolCall, Task<ToolResult>)>();
						foreach (ConversationToolCall toolCall in assistantMessage.ToolCalls)
						{
							Task<ToolResult> task = ExecuteTool(toolCall, tools, conversation.ToolContext);
							pendingTools.Add((toolCall, task));
						}

						// Await each task and add messages in order.
						foreach ((ConversationToolCall call, Task<ToolResult> task) in pendingTools)
						{
							ToolResult toolResult = await task;
							await conversation.AddToolMessageAsync(call.Id, toolResult.Response, cancellationToken);
						}

						// Check for exit after all messages are added.
						foreach ((ConversationToolCall call, Task<ToolResult> task) in pendingTools)
						{
							ToolResult toolResult = task.Result;
							if (toolResult.ExitLoop)
							{
								return (new LlmResult
								{
									ExitReason = LlmExitReason.ToolRequestedExit,
									Content = toolResult.Response,
									FinalToolCalled = call.Function.Name
								}, accumulatedCost);
							}
						}
					}
					else
					{
						// 200 but body indicates an error or unexpected format.
						transientRetries++;
						if (transientRetries >= maxTransientRetries)
						{
							MarkDown();
							return (new LlmResult
							{
								ExitReason = LlmExitReason.LlmCallFailed,
								ErrorMessage = response?.Error?.Message ?? "Empty response from API"
							}, accumulatedCost);
						}

						int waitSeconds = Math.Min(transientRetries * 3, 15);
						Console.WriteLine($"Invalid response from {_config.Model}, retrying in {waitSeconds}s ({transientRetries}/{maxTransientRetries})");
						await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
					}
				}
				else if (TryAdaptToError(httpResponse, responseBody))
				{
					// Configuration adapted, retry immediately.
				}
				else if (IsRateLimited(httpResponse, responseBody))
					{
						_availableAt = ComputeRetryAfterTime(httpResponse, responseBody);
						Console.WriteLine($"Rate limited by {_config.Model}, available at {_availableAt:HH:mm:ss}, body: {responseBody}");
						return (new LlmResult
					{
						ExitReason = LlmExitReason.RateLimited,
						RetryAfter = _availableAt
					}, accumulatedCost);
				}
				else if ((int)httpResponse.StatusCode >= 500)
				{
					transientRetries++;
					if (transientRetries >= maxTransientRetries)
					{
						MarkDown();
						return (new LlmResult
						{
							ExitReason = LlmExitReason.LlmCallFailed,
							ErrorMessage = $"Server error {httpResponse.StatusCode} after {transientRetries} retries"
						}, accumulatedCost);
					}

					int waitSeconds = Math.Min(transientRetries * 3, 15);
					Console.WriteLine($"Server error {httpResponse.StatusCode} from {_config.Model}, retrying in {waitSeconds}s ({transientRetries}/{maxTransientRetries})");
					await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
				}
				else
					{
						int statusCode = (int)httpResponse.StatusCode;

						// Auth errors are permanent — the key is wrong or access is revoked.
						if (statusCode == 401 || statusCode == 403)
						{
							MarkPermanentlyDown();
							return (new LlmResult
							{
								ExitReason = LlmExitReason.LlmCallFailed,
								ErrorMessage = $"Auth error {statusCode}: {responseBody}"
							}, accumulatedCost);
						}

						// Other 4xx errors (400 bad request, 422 unprocessable, etc.) are request-specific, not permanent.
						return (new LlmResult
						{
							ExitReason = LlmExitReason.LlmCallFailed,
							ErrorMessage = $"API error {statusCode}: {responseBody}"
						}, accumulatedCost);
					}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				transientRetries++;
				if (transientRetries >= maxTransientRetries)
				{
					MarkDown();
					return (new LlmResult
					{
						ExitReason = LlmExitReason.LlmCallFailed,
						ErrorMessage = $"Failed after {transientRetries} attempts: {ex.Message}"
					}, accumulatedCost);
				}

				int waitSeconds = Math.Min(transientRetries * 3, 15);
				Console.WriteLine($"Error from {_config.Model} (attempt {transientRetries}/{maxTransientRetries}): {ex.Message}. Retrying in {waitSeconds}s...");
				await Task.Delay(TimeSpan.FromSeconds(waitSeconds), cancellationToken);
			}
		}
	}

	private async Task<ToolResult> ExecuteTool(ConversationToolCall toolCall, List<Tool> tools, ToolContext context)
	{
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
					return await tool.Handler(args, context);
				}
				catch (Exception ex)
				{
					return new ToolResult($"Error: {ex.Message}", false);
				}
			}
		}

		return new ToolResult($"Error: Unknown tool '{toolCall.Function.Name}'", false);
	}

	// Checks whether a 4xx error is a known configuration mismatch and adapts settings for retry.
	// Returns true if an adaptation was made and the caller should retry immediately.
	private bool TryAdaptToError(HttpResponseMessage response, string responseBody)
	{
		int statusCode = (int)response.StatusCode;
		if (statusCode < 400 || statusCode >= 500 || statusCode == 429)
		{
			return false;
		}

		if (!_parallelToolCallsSupported)
		{
			return false;
		}

		string lowerBody = responseBody.ToLowerInvariant();

		// Specific match: error body mentions parallel_tool_calls.
		if (lowerBody.Contains("parallel_tool_calls") || lowerBody.Contains("parallel tool calls"))
		{
			_parallelToolCallsSupported = false;
			Console.WriteLine($"Model {_config.Model} does not support parallel_tool_calls, disabling");
			return true;
		}

		// Generic upstream error with no specific parameter mentioned.
		if (statusCode == 400 && (lowerBody.Contains("upstream_error") || lowerBody.Contains("provider returned error")))
		{
			_parallelToolCallsSupported = false;
			Console.WriteLine($"Model {_config.Model} generic upstream 400, disabling parallel_tool_calls");
			return true;
		}

		return false;
	}

	private void MarkDown()
	{
		_availableAt = DateTimeOffset.UtcNow + DownCooldown;
		Console.WriteLine($"LLM {_config.Model} marked down until {_availableAt:HH:mm:ss}");
	}

	private void MarkPermanentlyDown()
	{
		_isPermanentlyDown = true;
		Console.WriteLine($"LLM {_config.Model} permanently disabled due to non-recoverable error");
	}

	private DateTimeOffset ComputeRetryAfterTime(HttpResponseMessage response, string responseBody)
	{
		int seconds = ParseRateLimitSeconds(response, responseBody);
		if (seconds > 0)
		{
			return DateTimeOffset.UtcNow.AddSeconds(seconds);
		}

		return DateTimeOffset.UtcNow.AddSeconds(5);
	}

	private bool IsRateLimited(HttpResponseMessage response, string responseBody)
	{
		if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
		{
			return true;
		}

		if (responseBody.Contains("\"code\":429"))
		{
			return true;
		}

		return false;
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

	// Scans content for <tool_call> or <function_call> XML blocks containing JSON payloads.
	private List<ConversationToolCall>? TryParseXmlToolCalls(string content, List<Tool> tools)
	{
		List<ConversationToolCall> result = new List<ConversationToolCall>();
		string[] tagNames = ["tool_call", "function_call"];

		foreach (string tagName in tagNames)
		{
			string openTag = $"<{tagName}>";
			string closeTag = $"</{tagName}>";
			int searchStart = 0;

			while (searchStart < content.Length)
			{
				int openIndex = content.IndexOf(openTag, searchStart, StringComparison.OrdinalIgnoreCase);
				if (openIndex < 0)
				{
					break;
				}

				int contentStart = openIndex + openTag.Length;
				int closeIndex = content.IndexOf(closeTag, contentStart, StringComparison.OrdinalIgnoreCase);
				if (closeIndex < 0)
				{
					break;
				}

				string inner = content.Substring(contentStart, closeIndex - contentStart).Trim();
				searchStart = closeIndex + closeTag.Length;

				ConversationToolCall? toolCall = TryParseXmlToolCallJson(inner, tools);
				if (toolCall != null)
				{
					result.Add(toolCall);
				}
			}
		}

		if (result.Count == 0)
		{
			return null;
		}

		return result;
	}

	// Parses a single JSON payload from an XML tool call block, validates tool name and argument keys.
	private ConversationToolCall? TryParseXmlToolCallJson(string json, List<Tool> tools)
	{
		try
		{
			JsonObject? obj = JsonNode.Parse(json)?.AsObject();
			if (obj == null)
			{
				return null;
			}

			string? name = null;
			if (obj.TryGetPropertyValue("name", out JsonNode? nameNode))
			{
				name = nameNode?.GetValue<string>();
			}

			if (string.IsNullOrEmpty(name))
			{
				return null;
			}

			JsonObject args = new JsonObject();
			if (obj.TryGetPropertyValue("arguments", out JsonNode? argsNode) || obj.TryGetPropertyValue("parameters", out argsNode))
			{
				JsonObject? parsed = argsNode?.AsObject();
				if (parsed != null)
				{
					args = parsed;
				}
			}

			Tool? matchedTool = null;
			foreach (Tool tool in tools)
			{
				if (tool.Definition.Function.Name == name)
				{
					matchedTool = tool;
					break;
				}
			}

			if (matchedTool == null)
			{
				return null;
			}

			JsonObject? definedProperties = matchedTool.Definition.Function.Parameters["properties"]?.AsObject();

			foreach (KeyValuePair<string, JsonNode?> arg in args)
			{
				if (definedProperties == null || !definedProperties.ContainsKey(arg.Key))
				{
					return null;
				}
			}

			return new ConversationToolCall
			{
				Id = $"xmltc_{Guid.NewGuid():N}",
				Type = "function",
				Function = new ConversationFunctionCall
				{
					Name = name,
					Arguments = args.ToJsonString()
				}
			};
		}
		catch
		{
			return null;
		}
	}
}
