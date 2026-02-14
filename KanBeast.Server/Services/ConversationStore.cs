using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Server.Models;
using KanBeast.Shared;

namespace KanBeast.Server.Services;

// Stores full conversation data separately from tickets to keep ticket payloads small.
public class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConversationData>> _store = new();
    private readonly string _directory;
    private readonly ILogger<ConversationStore> _logger;

    public ConversationStore(ILogger<ConversationStore> logger)
    {
        _logger = logger;
        _directory = Path.Combine(Environment.CurrentDirectory, "conversations");
        Directory.CreateDirectory(_directory);
        LoadFromDisk();
    }

    public ConversationData? Get(string ticketId, string conversationId)
    {
        if (_store.TryGetValue(ticketId, out ConcurrentDictionary<string, ConversationData>? convos))
        {
            convos.TryGetValue(conversationId, out ConversationData? data);
            return data;
        }

        return null;
    }

    // Finds the active (unfinished) planning conversation for a ticket, if any.
    public ConversationData? GetActivePlanning(string ticketId)
    {
        if (!_store.TryGetValue(ticketId, out ConcurrentDictionary<string, ConversationData>? convos))
        {
            return null;
        }

        foreach ((string id, ConversationData data) in convos)
        {
            if (!data.IsFinished && data.DisplayName == "Planning")
            {
                return data;
            }
        }

        return null;
    }

    public List<ConversationInfo> GetInfoList(string ticketId)
    {
        List<ConversationInfo> result = new List<ConversationInfo>();

        if (_store.TryGetValue(ticketId, out ConcurrentDictionary<string, ConversationData>? convos))
        {
            foreach ((string id, ConversationData data) in convos)
            {
                result.Add(new ConversationInfo
                {
                    Id = data.Id,
                    DisplayName = data.DisplayName,
                    MessageCount = data.Messages.Count,
                    IsFinished = data.IsFinished
                });
            }
        }

        return result;
    }

    public async Task UpsertAsync(string ticketId, ConversationData data)
    {
        ConcurrentDictionary<string, ConversationData> convos = _store.GetOrAdd(ticketId, _ => new ConcurrentDictionary<string, ConversationData>());
        convos[data.Id] = data;
        await SaveToDiskAsync(ticketId);
    }

    public async Task FinishAsync(string ticketId, string conversationId)
    {
        if (!_store.TryGetValue(ticketId, out ConcurrentDictionary<string, ConversationData>? convos))
        {
            return;
        }

        if (!convos.TryGetValue(conversationId, out ConversationData? data))
        {
            return;
        }

        data.IsFinished = true;
        await SaveToDiskAsync(ticketId);
    }

    public void DeleteForTicket(string ticketId)
    {
        _store.TryRemove(ticketId, out _);
        string path = Path.Combine(_directory, $"convos-{ticketId}.json");

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task SaveToDiskAsync(string ticketId)
    {
        if (!_store.TryGetValue(ticketId, out ConcurrentDictionary<string, ConversationData>? convos))
        {
            return;
        }

        string path = Path.Combine(_directory, $"convos-{ticketId}.json");
        Dictionary<string, ConversationData> snapshot = new Dictionary<string, ConversationData>(convos);
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    private void LoadFromDisk()
    {
        int count = 0;

        foreach (string filePath in Directory.EnumerateFiles(_directory, "convos-*.json"))
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                string ticketId = fileName.Substring("convos-".Length);

                string json = File.ReadAllText(filePath);
                Dictionary<string, ConversationData>? data = JsonSerializer.Deserialize<Dictionary<string, ConversationData>>(json, JsonOptions);

                if (data != null)
                {
                    ConcurrentDictionary<string, ConversationData> convos = new ConcurrentDictionary<string, ConversationData>(data);
                    _store[ticketId] = convos;
                    count += convos.Count;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to load conversations from {Path}: {Error}", filePath, ex.Message);
            }
        }

        _logger.LogInformation("Loaded {Count} conversations from disk", count);
    }
}
