using System.Text.Json;
using System.Text.Json.Serialization;

namespace KanBeast.Worker.Services;

// Holds the content and logging state for a single LLM conversation.
public class LlmConversation
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _logPath;

    public LlmConversation(string model, string systemPrompt, string userPrompt, string logPath)
    {
        Model = model;
        StartedAt = DateTime.UtcNow.ToString("O");
        CompletedAt = string.Empty;
        Messages = new List<ChatMessage>();
        _logPath = logPath;

        ChatMessage systemMessage = new ChatMessage { Role = "system", Content = systemPrompt };
        ChatMessage userMessage = new ChatMessage { Role = "user", Content = userPrompt };
        Messages.Add(systemMessage);
        Messages.Add(userMessage);

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            string? directory = Path.GetDirectoryName(_logPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string header = $"# LLM Conversation Log\n**Model:** {Model}\n**Started:** {StartedAt}\n---\n\n";
            File.WriteAllText(_logPath, header);
            AppendMessageToLog(systemMessage);
            AppendMessageToLog(userMessage);
        }
    }

    [JsonPropertyName("model")]
    public string Model { get; }

    [JsonPropertyName("started_at")]
    public string StartedAt { get; }

    [JsonPropertyName("completed_at")]
    public string CompletedAt { get; private set; }

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; }

    public void AddAssistantMessage(ChatMessage message)
    {
        Messages.Add(message);
        AppendMessageToLog(message);
    }

    public void AddToolMessage(string toolCallId, string toolResult)
    {
        ChatMessage message = new ChatMessage
        {
            Role = "tool",
            Content = toolResult,
            ToolCallId = toolCallId
        };
        Messages.Add(message);
        AppendMessageToLog(message);
    }

    public void MarkCompleted()
    {
        CompletedAt = DateTime.UtcNow.ToString("O");

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            File.AppendAllText(_logPath, $"\n---\n**Completed:** {CompletedAt}\n");
        }
    }

    private void AppendMessageToLog(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            File.AppendAllText(_logPath, FormatMessage(message));
        }
    }

    private static string FormatMessage(ChatMessage message)
    {
        if (message.Role == "system")
        {
            return $"## System:\n{message.Content ?? string.Empty}\n\n";
        }

        if (message.Role == "user")
        {
            return $"## User:\n{message.Content ?? string.Empty}\n\n";
        }

        if (message.Role == "assistant")
        {
            string result = "## Assistant:\n";
            if (!string.IsNullOrEmpty(message.Content))
            {
                result += $"{message.Content}\n\n";
            }

            if (message.ToolCalls != null && message.ToolCalls.Count > 0)
            {
                foreach (ToolCallMessage toolCall in message.ToolCalls)
                {
                    result += $"**Tool call:** {toolCall.Function.Name} {toolCall.Function.Arguments}\n";
                }
                result += "\n";
            }

            return result;
        }

        if (message.Role == "tool")
        {
            return $"**Tool result:**\n```\n{message.Content ?? string.Empty}\n```\n\n";
        }

        return string.Empty;
    }

    public async Task WriteLogAsync(string suffix, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_logPath))
        {
            return;
        }

        string jsonPath = Path.ChangeExtension(_logPath, null) + suffix + ".json";

        string? directory = Path.GetDirectoryName(jsonPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string content = JsonSerializer.Serialize(this, JsonOptions);
        await File.WriteAllTextAsync(jsonPath, content, cancellationToken);
    }
}
