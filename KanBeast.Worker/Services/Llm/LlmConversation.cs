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

    public async Task WriteLogAsync(string logDirectory, string logPrefix, string reason, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);

            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string suffix = reason.Replace(" ", "-");
            string filename = !string.IsNullOrEmpty(logPrefix)
                ? $"{logPrefix}-{timestamp}-{suffix}.json"
                : $"session-{timestamp}-{suffix}.json";
            string logPath = Path.Combine(logDirectory, filename);

            int duplicateIndex = 1;
            while (File.Exists(logPath))
            {
                string duplicateName = !string.IsNullOrEmpty(logPrefix)
                    ? $"{logPrefix}-{timestamp}-{suffix}-{duplicateIndex}.json"
                    : $"session-{timestamp}-{suffix}-{duplicateIndex}.json";
                logPath = Path.Combine(logDirectory, duplicateName);
                duplicateIndex++;
            }

            string json = JsonSerializer.Serialize(this, JsonOptions);
            await File.WriteAllTextAsync(logPath, json, cancellationToken);
        }
    }
}
