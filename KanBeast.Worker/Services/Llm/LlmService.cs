using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
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
	RateLimited,           // Rate limited, RetryAfter indicates when to retry
	Interrupted,           // User interrupted the conversation via the hub
	ModelChanged,          // User switched the conversation's LLM model
	RepetitionDetected     // Model repeated the same action too many times
}

public class LlmResult
{
	public LlmExitReason ExitReason { get; }
	public bool Success => ExitReason == LlmExitReason.Completed || ExitReason == LlmExitReason.ToolRequestedExit;
	public string Content { get; }
	public string ErrorMessage { get; }
	public string? FinalToolCalled { get; }
	public DateTimeOffset? RetryAfter { get; }

	public LlmResult(LlmExitReason exitReason, string content, string errorMessage, string? finalToolCalled, DateTimeOffset? retryAfter)
	{
		ExitReason = exitReason;
		Content = content;
		ErrorMessage = errorMessage;
		FinalToolCalled = finalToolCalled;
		RetryAfter = retryAfter;
	}
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

	private const int ShortWaitThresholdSeconds = 20;
	private const int RepetitionWarningStart = 3;
	private const int RepetitionLimit = 5;

	private readonly Random SeedRandom = new();

	private readonly LLMConfig _config;
	private readonly HttpClient _httpClient;
	private readonly string _endpoint;

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

	public LlmService(LLMConfig config, string endpoint, string apiKey)
	{
		_config = config;
		_endpoint = endpoint.TrimEnd('/');

		if (string.IsNullOrWhiteSpace(endpoint))
		{
			throw new InvalidOperationException("LLM endpoint is required, such as https://openrouter.ai/api/v1");
		}

		_httpClient = new HttpClient();
		_httpClient.Timeout = TimeSpan.FromMinutes(5);
		_httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
	}

	// Runs the conversation in a loop until idle, tool-exit, or fatal error.
	// Handles availability, rate-limit waits, budget, and per-conversation interrupt CTS.
	public async Task<LlmResult> RunToCompletionAsync(ILlmConversation conversation, string? continueMessage, bool continueOnToolExit, bool finalizeOnExit, CancellationToken cancellationToken)
	{
		LlmResult result;

		if (!_isPermanentlyDown && (IsAvailable || (_availableAt - DateTimeOffset.UtcNow).TotalSeconds <= ShortWaitThresholdSeconds))
		{
			if (!IsAvailable)
			{
				TimeSpan waitTime = _availableAt - DateTimeOffset.UtcNow;
				if (waitTime > TimeSpan.Zero)
				{
					Console.WriteLine($"LLM {_config.Model} rate limited, waiting {waitTime.TotalSeconds:F0}s...");
					await Task.Delay(waitTime, cancellationToken);
				}
			}

			result = await ExecuteConversationAsync(conversation, continueMessage, continueOnToolExit, finalizeOnExit, cancellationToken);

			// If the user switched models mid-conversation, the old service's loop exited cleanly.
			// Trampoline to the new service so the switch is invisible to callers.
			if (result.ExitReason == LlmExitReason.ModelChanged)
			{
				LlmService? newService = conversation.ToolContext.LlmService;
				if (newService != null && newService != this)
				{
					result = await newService.RunToCompletionAsync(conversation, continueMessage, continueOnToolExit, finalizeOnExit, cancellationToken);
				}
			}
		}
		else if (_isPermanentlyDown)
		{
			result = new LlmResult(LlmExitReason.LlmCallFailed, string.Empty, $"LLM {_config.Model} is permanently down", null, null);
		}
		else
		{
			result = new LlmResult(LlmExitReason.RateLimited, string.Empty, $"LLM {_config.Model} rate limited for {(_availableAt - DateTimeOffset.UtcNow).TotalSeconds:F0}s", null, _availableAt);
		}

		return result;
	}

	private async Task<LlmResult> ExecuteConversationAsync(ILlmConversation conversation, string? continueMessage, bool continueOnToolExit, bool finalizeOnExit, CancellationToken cancellationToken)
	{
		CancellationToken conversationToken = WorkerSession.HubClient.RegisterConversation(conversation.Id, cancellationToken);

		// Separate CTS for tools, linked to the conversation token. When this conversation is
		// interrupted (directly or via parent), we cancel toolCts first so running tools and
		// sub-agents see the cancellation before this conversation exits.
		using CancellationTokenSource toolCts = CancellationTokenSource.CreateLinkedTokenSource(conversationToken);
		conversation.ToolContext.CancellationToken = toolCts.Token;

		decimal accumulatedCost = 0m;
		LlmResult finalResult = new LlmResult(LlmExitReason.Completed, string.Empty, string.Empty, null, null);

		try
		{
			List<Tool> tools = ToolsFactory.GetTools(conversation.Role);
			List<ToolDefinition>? toolDefs = tools.Count > 0 ? new List<ToolDefinition>() : null;
			foreach (Tool tool in tools)
			{
				toolDefs?.Add(tool.Definition);
			}

			int transientRetries = 0;
			int maxTransientRetries = _hasSucceeded ? 3 : 1;

			for (; ; )
			{
				// Heartbeat every iteration so the server watchdog knows this worker is alive.
				// This covers all agent types (planning, developer, subagents) since they all
				// run through this loop. Without this, long LLM calls starve the orchestrator's
				// idle-loop heartbeat and the watchdog kills the worker.
				await WorkerSession.HubClient.SendHeartbeatAsync();

				if (conversation.HasReachedMaxIterations)
					{
						string recentContext = ExtractRecentAssistantContext(conversation.Messages, 3);
						finalResult = new LlmResult(LlmExitReason.MaxIterationsReached, recentContext, "Max iterations reached", null, null);
						break;
					}

				decimal remainingBudget = conversation.GetRemainingBudget();
				if (remainingBudget > 0 && accumulatedCost >= remainingBudget)
				{
					finalResult = new LlmResult(LlmExitReason.CostExceeded, string.Empty, "Cost budget exceeded", null, null);
					break;
				}

				conversationToken.ThrowIfCancellationRequested();

				// Handle clear-conversation requests before injecting new messages.
				if (WorkerSession.HubClient.TryConsumeClearRequest(conversation.Id))
				{
					Console.WriteLine($"[{conversation.Id}] Clear request received, resetting conversation");
					await conversation.ResetAsync();
					finalResult = new LlmResult(LlmExitReason.Completed, string.Empty, string.Empty, null, null);
					break;
				}

				// Inject any pending user messages from the chat hub.
				ConcurrentQueue<string> chatQueue = WorkerSession.GetChatQueue(conversation.Id);
				while (chatQueue.TryDequeue(out string? chatMsg))
				{
					conversation.AddUserMessage(chatMsg);
				}

				// Check for pending model changes from the hub.
				string? newLlmConfigId = WorkerSession.HubClient.TryConsumeModelChange(conversation.Id);
				if (newLlmConfigId != null)
				{
					// Apply any pending settings so newly added LLMs are in the registry
					// before the lookup. Settings and model-change events often arrive
					// together, but settings are normally only applied in the outer loop.
					WorkerSession.ApplyPendingSettings();

					LlmService? newService = WorkerSession.LlmProxy.GetService(newLlmConfigId);
					if (newService != null)
					{
						conversation.ToolContext.LlmService = newService;
						conversation.AddNote($"Model switched to {newService.Model}");
						finalResult = new LlmResult(LlmExitReason.ModelChanged, string.Empty, string.Empty, null, null);
						break;
					}
					else
					{
						Console.WriteLine($"[{conversation.Id}] Model change to '{newLlmConfigId}' failed: config not found in registry, re-queuing");
						WorkerSession.HubClient.RequeueModelChange(conversation.Id, newLlmConfigId);
					}
				}

				// Some providers (e.g. Anthropic) reject conversations that don't end with a user or tool message.
				// Build the message list for the request, appending a kickoff if the last message is not user/tool.
				List<ConversationMessage> requestMessages = conversation.Messages;
				if (requestMessages.Count > 0)
				{
					string lastRole = requestMessages[requestMessages.Count - 1].Role;
					if (lastRole != "user" && lastRole != "tool")
					{
						string kickoff = BuildContinueMessageWithWarning(continueMessage ?? "Continue.", conversation.Iteration, conversation.MaxIterations);
						requestMessages = new List<ConversationMessage>(requestMessages);
						requestMessages.Add(new ConversationMessage { Role = "user", Content = kickoff });
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
					MaxCompletionTokens = null,
					MaxTokens = null
				};

				string requestJson = JsonSerializer.Serialize(request, JsonOptions);
				string fullUrl = $"{_endpoint}/chat/completions";

				try
				{
					StringContent content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
					HttpResponseMessage httpResponse = await _httpClient.PostAsync(fullUrl, content, conversationToken);
					string responseBody = await httpResponse.Content.ReadAsStringAsync(conversationToken);

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

							LlmResult? responseResult = await ProcessAssistantResponseAsync(assistantMessage, tools, conversation, continueMessage, continueOnToolExit, conversationToken);
							await conversation.MaintenanceAsync(conversationToken);

							if (responseResult != null)
							{
								finalResult = responseResult;
								break;
							}
						}
						else
						{
							// 200 but body indicates an error or unexpected format.
							transientRetries++;
							if (transientRetries >= maxTransientRetries)
							{
								MarkDown();
								finalResult = new LlmResult(LlmExitReason.LlmCallFailed, string.Empty, response?.Error?.Message ?? "Empty response from API", null, null);
								break;
							}

							int waitSeconds = Math.Min(transientRetries * 3, 15);
							Console.WriteLine($"Invalid response from {_config.Model}, retrying in {waitSeconds}s ({transientRetries}/{maxTransientRetries})");
							await Task.Delay(TimeSpan.FromSeconds(waitSeconds), conversationToken);
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
						finalResult = new LlmResult(LlmExitReason.RateLimited, string.Empty, string.Empty, null, _availableAt);
						break;
					}
					else if ((int)httpResponse.StatusCode >= 500)
					{
						transientRetries++;
						if (transientRetries >= maxTransientRetries)
						{
							MarkDown();
							finalResult = new LlmResult(LlmExitReason.LlmCallFailed, string.Empty, $"Server error {httpResponse.StatusCode} after {transientRetries} retries", null, null);
							break;
						}

						int waitSeconds = Math.Min(transientRetries * 3, 15);
						Console.WriteLine($"Server error {httpResponse.StatusCode} from {_config.Model}, retrying in {waitSeconds}s ({transientRetries}/{maxTransientRetries})");
						await Task.Delay(TimeSpan.FromSeconds(waitSeconds), conversationToken);
					}
					else
					{
						int statusCode = (int)httpResponse.StatusCode;

						// Auth errors are permanent — the key is wrong or access is revoked.
						if (statusCode == 401 || statusCode == 403)
						{
							MarkPermanentlyDown();
							finalResult = new LlmResult(LlmExitReason.LlmCallFailed, string.Empty, $"Auth error {statusCode}: {responseBody}", null, null);
							break;
						}

						// Other 4xx errors (400 bad request, 422 unprocessable, etc.) are request-specific, not permanent.
						finalResult = new LlmResult(LlmExitReason.LlmCallFailed, string.Empty, $"API error {statusCode}: {responseBody}", null, null);
						break;
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
						finalResult = new LlmResult(LlmExitReason.LlmCallFailed, string.Empty, $"Failed after {transientRetries} attempts: {ex.Message}", null, null);
						break;
					}

					int waitSeconds = Math.Min(transientRetries * 3, 15);
					Console.WriteLine($"Error from {_config.Model} (attempt {transientRetries}/{maxTransientRetries}): {ex.Message}. Retrying in {waitSeconds}s...");
					await Task.Delay(TimeSpan.FromSeconds(waitSeconds), conversationToken);
				}
			}
		}
		catch (OperationCanceledException)
		{
			// Cancel tool CTS so any running tools/sub-agents see it and add their own notes.
			toolCts.Cancel();

			if (!cancellationToken.IsCancellationRequested)
			{
				// Direct interrupt: the user clicked stop on this conversation.
				conversation.AddNote("Interrupted by user.");
				finalResult = new LlmResult(LlmExitReason.Interrupted, string.Empty, "Interrupted by user", null, null);
			}
			else
			{
				// Parent cancelled (e.g., planning agent interrupted while this sub-agent was running).
				conversation.AddNote("Parent conversation was interrupted.");
				throw;
			}
		}
		finally
		{
			await conversation.RecordCostAsync(accumulatedCost, CancellationToken.None);
			await conversation.ForceFlushAsync();
			WorkerSession.HubClient.UnregisterConversation(conversation.Id);
		}

		if (finalizeOnExit && finalResult.ExitReason != LlmExitReason.ModelChanged)
		{
			string finalContent = await conversation.FinalizeAsync(finalResult, cancellationToken);
			finalResult = new LlmResult(finalResult.ExitReason, finalContent, finalResult.ErrorMessage, finalResult.FinalToolCalled, finalResult.RetryAfter);
		}
		else
		{
			await conversation.ForceFlushAsync();
		}

		return finalResult;
	}

	private async Task<ToolResult> ExecuteTool(ConversationToolCall toolCall, List<Tool> tools, ToolContext context)
	{
		ToolContext.ActiveToolCallId = toolCall.Id;
		ToolResult result = new ToolResult($"Error: Unknown tool '{toolCall.Function.Name}'", false, false);

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
					result = await tool.Handler(args, context);
				}
				catch (Exception ex)
				{
					result = new ToolResult($"Error: {ex.Message}", false, false);
				}

				break;
			}
		}

		return result;
	}

	// Processes the assistant's response: normalizes tool calls, executes them, and determines next action.
	// Returns null to continue the loop, or an LlmResult to break out.
	private async Task<LlmResult?> ProcessAssistantResponseAsync(ConversationMessage assistantMessage, List<Tool> tools, ILlmConversation conversation, string? continueMessage, bool continueOnToolExit, CancellationToken conversationToken)
	{
		LlmResult? result = null;

		// Trim content; some models return whitespace-only assistant messages.
		if (assistantMessage.Content != null)
		{
			assistantMessage.Content = assistantMessage.Content.Trim();
			if (assistantMessage.Content.Length == 0)
			{
				assistantMessage.Content = null;
			}
		}

		// If the message is completely empty (no content, no tool calls), skip it.
		bool hasToolCalls = assistantMessage.ToolCalls != null && assistantMessage.ToolCalls.Count > 0;
		if (assistantMessage.Content == null && !hasToolCalls)
		{
			Console.WriteLine($"[{_config.Model}] Skipping empty assistant message");
			return null;
		}

		// Trim whitespace from function names and argument keys that some models produce.
		if (assistantMessage.ToolCalls != null && assistantMessage.ToolCalls.Count > 0)
		{
			NormalizeToolCalls(assistantMessage.ToolCalls);
		}

		// If no native tool calls, try parsing XML-style tool calls from content.
		if ((assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0) && !string.IsNullOrEmpty(assistantMessage.Content))
		{
			List<ConversationToolCall>? xmlToolCalls = TryParseXmlToolCalls(assistantMessage.Content, tools);
			if (xmlToolCalls != null)
			{
				NormalizeToolCalls(xmlToolCalls);
				assistantMessage.ToolCalls = xmlToolCalls;
				Console.WriteLine($"Parsed {xmlToolCalls.Count} XML-style tool call(s) from {_config.Model} response");
			}
		}

		// Detect repetitive identical actions from the model.
		uint fingerprint = FingerprintAssistantMessage(assistantMessage);
		Dictionary<uint, int> fingerprints = conversation.ToolContext.ActionFingerprints;
		fingerprints.TryGetValue(fingerprint, out int repeatCount);
		repeatCount++;
		fingerprints[fingerprint] = repeatCount;

		if (repeatCount >= RepetitionLimit)
		{
			Console.WriteLine($"[{_config.Model}] Terminating: identical action repeated {repeatCount} times");
			conversation.AddAssistantMessage(assistantMessage);
			conversation.AddNote($"TERMINATED: Model repeated the same action {repeatCount} times without making progress.");
			string recentContext = ExtractRecentAssistantContext(conversation.Messages, 3);
			return new LlmResult(LlmExitReason.RepetitionDetected, recentContext, $"Identical action repeated {repeatCount} times", null, null);
		}

		if (repeatCount >= RepetitionWarningStart)
		{
			Console.WriteLine($"[{_config.Model}] Repetition warning: identical action seen {repeatCount}/{RepetitionLimit} times");
		}

		conversation.AddAssistantMessage(assistantMessage);

		if (assistantMessage.ToolCalls == null || assistantMessage.ToolCalls.Count == 0)
		{
			if (continueMessage == null)
			{
				result = new LlmResult(LlmExitReason.Completed, assistantMessage.Content ?? string.Empty, string.Empty, null, null);
			}
			else
			{
				ConcurrentQueue<string> continueQueue = WorkerSession.GetChatQueue(conversation.Id);
				if (continueQueue.IsEmpty)
				{
					string effective = BuildContinueMessageWithWarning(continueMessage!, conversation.Iteration, conversation.MaxIterations);
					if (repeatCount >= RepetitionWarningStart)
					{
						effective += $"\n\n⚠️ REPETITION WARNING: You have repeated this exact same response {repeatCount} time(s). You will be TERMINATED at {RepetitionLimit}. Do something different or call your completion tool NOW.";
					}
					conversation.AddUserMessage(effective);
				}
			}
		}
		else
		{
			// Concurrent tool execution. All tool calls from the same assistant
			// message run as parallel Tasks. Results are collected in order.
			List<ConversationToolCall> toolCalls = assistantMessage.ToolCalls;
			(string toolName, ToolResult toolResult)[] completedTools = new (string, ToolResult)[toolCalls.Count];

			Task[] tasks = new Task[toolCalls.Count];
			for (int i = 0; i < toolCalls.Count; i++)
			{
				int index = i;
				ConversationToolCall toolCall = toolCalls[index];
				tasks[index] = Task.Run(async () =>
				{
					ToolResult toolResult = await ExecuteTool(toolCall, tools, conversation.ToolContext);
					completedTools[index] = (toolCall.Function.Name, toolResult);
				}, conversationToken);
			}

			await Task.WhenAll(tasks);

			// Add tool messages in order and check for exit/message-handled.
			for (int i = 0; i < completedTools.Length; i++)
			{
				(string toolName, ToolResult toolResult) = completedTools[i];

				if (!toolResult.MessageHandled)
				{
					string response = toolResult.Response;
					if (repeatCount >= RepetitionWarningStart && i == completedTools.Length - 1)
					{
						response += $"\n\n⚠️ REPETITION WARNING: You have made this exact same tool call {repeatCount} time(s). You will be TERMINATED at {RepetitionLimit}. Do something different or call your completion tool NOW.";
					}
					conversation.AddToolMessage(toolCalls[i].Id, response);
				}

				if (toolResult.ExitLoop)
				{
					if (!continueOnToolExit)
					{
						result = new LlmResult(LlmExitReason.ToolRequestedExit, toolResult.Response, string.Empty, toolName, null);
						break;
					}

					await conversation.ForceFlushAsync();
				}

				if (toolResult.MessageHandled)
				{
					break;
				}
			}
		}

		return result;
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

	// Substitutes {messagesRemaining} into the continue message and appends iteration warnings.
	// Every 50 iterations: informational note. Last 5 iterations: urgent warning each turn.
	private static string BuildContinueMessageWithWarning(string message, int iteration, int maxIterations)
	{
		int messagesRemaining = maxIterations - iteration;
		string effective = message.Replace("{messagesRemaining}", messagesRemaining.ToString());

		if (messagesRemaining <= 5)
		{
			effective += $"\n\n⚠️ URGENT: Only {messagesRemaining} messages remaining before this conversation is terminated. Call your completion function NOW or all work from this session will be lost.";
		}
		else if (iteration > 0 && iteration % 50 == 0)
		{
			effective += $"\n\nNote: {iteration} of {maxIterations} messages used ({messagesRemaining} remaining).";
		}

		return effective;
	}

	// IEEE 802.3 CRC32 lookup table, computed once.
	private static readonly uint[] Crc32Table = BuildCrc32Table();

	private static uint[] BuildCrc32Table()
	{
		uint[] table = new uint[256];
		for (uint i = 0; i < 256; i++)
		{
			uint crc = i;
			for (int j = 0; j < 8; j++)
			{
				if ((crc & 1) != 0)
				{
					crc = (crc >> 1) ^ 0xEDB88320u;
				}
				else
				{
					crc >>= 1;
				}
			}
			table[i] = crc;
		}
		return table;
	}

	private static uint ComputeCrc32(byte[] data)
	{
		uint crc = 0xFFFFFFFFu;
		for (int i = 0; i < data.Length; i++)
		{
			crc = Crc32Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
		}
		return crc ^ 0xFFFFFFFFu;
	}

	// Builds a stable fingerprint from the assistant message content and tool call signatures.
	// Tool call IDs are excluded since they are unique each time.
	private static uint FingerprintAssistantMessage(ConversationMessage message)
	{
		StringBuilder sb = new StringBuilder();

		if (message.Content != null)
		{
			sb.Append(message.Content);
		}

		if (message.ToolCalls != null)
		{
			foreach (ConversationToolCall tc in message.ToolCalls)
			{
				sb.Append('\0');
				sb.Append(tc.Function.Name);
				sb.Append('\0');
				sb.Append(tc.Function.Arguments);
			}
		}

		byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
		return ComputeCrc32(bytes);
	}

	// Extracts the last N assistant messages (with any immediately following tool results) for context
	// when a conversation is terminated. Callers get useful work product instead of an empty string.
	private static string ExtractRecentAssistantContext(List<ConversationMessage> messages, int count)
	{
		StringBuilder sb = new StringBuilder();
		int found = 0;

		for (int i = messages.Count - 1; i >= 0 && found < count; i--)
		{
			ConversationMessage msg = messages[i];
			if (msg.Role != "assistant")
			{
				continue;
			}

			found++;
			sb.Insert(0, "\n---\n");

			// Collect tool results that immediately follow this assistant message.
			for (int j = i + 1; j < messages.Count && messages[j].Role == "tool"; j++)
			{
				string toolSnippet = messages[j].Content ?? string.Empty;
				if (toolSnippet.Length > 500)
				{
					toolSnippet = toolSnippet.Substring(0, 500) + "…";
				}
				sb.Insert(0, $"[tool result] {toolSnippet}\n");
			}

			if (msg.ToolCalls != null)
			{
				foreach (ConversationToolCall tc in msg.ToolCalls)
				{
					sb.Insert(0, $"[called {tc.Function.Name}] {tc.Function.Arguments}\n");
				}
			}

			if (!string.IsNullOrEmpty(msg.Content))
			{
				sb.Insert(0, msg.Content + "\n");
			}
		}

		return sb.ToString().Trim();
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

	// Normalizes tool calls: assigns clean GUIDs as IDs, trims function names and argument keys.
	private static void NormalizeToolCalls(List<ConversationToolCall> toolCalls)
	{
		foreach (ConversationToolCall tc in toolCalls)
		{
			tc.Id = Guid.NewGuid().ToString();
			tc.Function.Name = tc.Function.Name.Trim();

			try
			{
				JsonObject? argsObj = JsonNode.Parse(tc.Function.Arguments)?.AsObject();
				if (argsObj != null)
				{
					List<(string key, JsonNode? value)> entries = new List<(string, JsonNode?)>();
					foreach (KeyValuePair<string, JsonNode?> kvp in argsObj)
					{
						entries.Add((kvp.Key.Trim(), kvp.Value));
					}

					JsonObject trimmed = new JsonObject();
					foreach ((string key, JsonNode? value) in entries)
					{
						trimmed[key] = value?.DeepClone();
					}

					tc.Function.Arguments = trimmed.ToJsonString();
				}
			}
			catch
			{
			}
		}
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
