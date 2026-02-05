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

    public LlmConversation(string model, string systemPrompt, string userPrompt)
    {
        Model = model;
        StartedAt = DateTime.UtcNow.ToString("O");
        CompletedAt = string.Empty;
        Messages = new List<ChatMessage>();

        Messages.Add(new ChatMessage { Role = "system", Content = systemPrompt });
        Messages.Add(new ChatMessage { Role = "user", Content = userPrompt });
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
    }

    public void AddToolMessage(string toolCallId, string toolResult)
    {
        Messages.Add(new ChatMessage
        {
            Role = "tool",
            Content = toolResult,
            ToolCallId = toolCallId
        });
    }

    public void MarkCompleted()
    {
        CompletedAt = DateTime.UtcNow.ToString("O");
    }

    public async Task WriteLogAsync(string logDirectory, string logPrefix, string reason, bool jsonFormat, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string suffix = reason.Replace(" ", "-");
            string extension = ".log";
            string filename = !string.IsNullOrEmpty(logPrefix)
                ? $"{logPrefix}-{timestamp}-{suffix}{extension}"
                : $"session-{timestamp}-{suffix}{extension}";
            string logPath = Path.Combine(logDirectory, filename);

            int duplicateIndex = 1;
            while (File.Exists(logPath))
            {
                string duplicateName = !string.IsNullOrEmpty(logPrefix)
                    ? $"{logPrefix}-{timestamp}-{suffix}-{duplicateIndex}{extension}"
                    : $"session-{timestamp}-{suffix}-{duplicateIndex}{extension}";
                logPath = Path.Combine(logDirectory, duplicateName);
                duplicateIndex++;
            }

            string content = jsonFormat ? JsonSerializer.Serialize(this, JsonOptions) : FormatFriendlyLog();
            await File.WriteAllTextAsync(logPath, content, cancellationToken);
        }
    }

    private string FormatFriendlyLog()
    {
        System.Text.StringBuilder builder = new System.Text.StringBuilder();
        builder.AppendLine($"# LLM Conversation Log");
        builder.AppendLine();
        builder.AppendLine($"**Model:** {Model}");
        builder.AppendLine($"**Started:** {StartedAt}");
        if (!string.IsNullOrEmpty(CompletedAt))
        {
            builder.AppendLine($"**Completed:** {CompletedAt}");
        }
        builder.AppendLine();
        builder.AppendLine("---");
        builder.AppendLine();

        foreach (ChatMessage message in Messages)
        {
            if (message.Role == "system")
            {
                builder.AppendLine($"## System:");
                builder.AppendLine(message.Content ?? string.Empty);
                builder.AppendLine();
            }
            else if (message.Role == "user")
            {
                builder.AppendLine($"## User:");
                builder.AppendLine(message.Content ?? string.Empty);
                builder.AppendLine();
            }
            else if (message.Role == "assistant")
            {
                builder.AppendLine($"## Assistant:");
                if (!string.IsNullOrEmpty(message.Content))
                {
                    builder.AppendLine(message.Content);
                    builder.AppendLine();
                }

                if (message.ToolCalls != null && message.ToolCalls.Count > 0)
                {
                    foreach (ToolCallMessage toolCall in message.ToolCalls)
                    {
                        builder.AppendLine($"**Tool call:** {toolCall.Function.Name} {toolCall.Function.Arguments}");
                    }
                    builder.AppendLine();
                }
            }
            else if (message.Role == "tool")
            {
                builder.AppendLine($"**Tool result:**");
                builder.AppendLine("```");
                builder.AppendLine(message.Content ?? string.Empty);
                builder.AppendLine("```");
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }
}
