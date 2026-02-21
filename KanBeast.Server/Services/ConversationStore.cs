using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using KanBeast.Server.Models;
using KanBeast.Shared;

namespace KanBeast.Server.Services;

// Stores full conversation data separately from tickets to keep ticket payloads small.
// Reads and writes directly to disk with no in-memory cache, so edits to the JSON
// files are immediately visible without restarting the server.
// All access to a given ticket's file is serialized via a per-ticket lock.
public class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions ReadOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();
    private readonly string _directory;
    private readonly ILogger<ConversationStore> _logger;

    public ConversationStore(ILogger<ConversationStore> logger)
    {
        _logger = logger;
        _directory = Path.Combine(Environment.CurrentDirectory, "conversations");
        Directory.CreateDirectory(_directory);
    }

    private SemaphoreSlim GetLock(string ticketId)
    {
        return _locks.GetOrAdd(ticketId, _ => new SemaphoreSlim(1, 1));
    }

    public ConversationData? Get(string ticketId, string conversationId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        fileLock.Wait();
        try
        {
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);
            convos.TryGetValue(conversationId, out ConversationData? data);
            return data;
        }
        finally
        {
            fileLock.Release();
        }
    }

    // Finds the active (unfinished) planning conversation for a ticket, if any.
    public ConversationData? GetActivePlanning(string ticketId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        fileLock.Wait();
        try
        {
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);

            foreach ((string id, ConversationData data) in convos)
            {
                if (!data.IsFinished && data.DisplayName == "Planning")
                {
                    return data;
                }
            }

            return null;
        }
        finally
        {
            fileLock.Release();
        }
    }

    // Returns all non-finalized conversations for a ticket.
    public List<ConversationData> GetNonFinalized(string ticketId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        fileLock.Wait();
        try
        {
            List<ConversationData> result = new List<ConversationData>();
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);

            foreach ((string id, ConversationData data) in convos)
            {
                if (!data.IsFinished)
                {
                    result.Add(data);
                }
            }

            return result;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public List<ConversationInfo> GetInfoList(string ticketId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        fileLock.Wait();
        try
        {
            List<ConversationInfo> result = new List<ConversationInfo>();
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);

            foreach ((string id, ConversationData data) in convos)
            {
                result.Add(new ConversationInfo
                {
                    Id = data.Id,
                    DisplayName = data.DisplayName,
                    MessageCount = data.Messages.Count,
                    IsFinished = data.IsFinished,
                    StartedAt = data.StartedAt,
                    ActiveModel = data.ActiveModel,
                    Role = data.Role
                });
            }

            return result;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task UpsertAsync(string ticketId, ConversationData data)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        await fileLock.WaitAsync();
        try
        {
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);
            convos[data.Id] = data;
            await SaveFileAsync(ticketId, convos);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task FinishAsync(string ticketId, string conversationId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        await fileLock.WaitAsync();
        try
        {
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);

            if (!convos.TryGetValue(conversationId, out ConversationData? data))
            {
                return;
            }

            data.IsFinished = true;
            await SaveFileAsync(ticketId, convos);
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string ticketId, string conversationId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        await fileLock.WaitAsync();
        try
        {
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);

            if (!convos.Remove(conversationId))
            {
                return false;
            }

            await SaveFileAsync(ticketId, convos);
            return true;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public async Task<int> DeleteFinishedAsync(string ticketId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        await fileLock.WaitAsync();
        try
        {
            Dictionary<string, ConversationData> convos = LoadFile(ticketId);
            int removed = 0;

            List<string> toRemove = new List<string>();
            foreach ((string id, ConversationData data) in convos)
            {
                if (data.IsFinished)
                {
                    toRemove.Add(id);
                }
            }

            foreach (string id in toRemove)
            {
                convos.Remove(id);
                removed++;
            }

            if (removed > 0)
            {
                await SaveFileAsync(ticketId, convos);
            }

            return removed;
        }
        finally
        {
            fileLock.Release();
        }
    }

    public void DeleteForTicket(string ticketId)
    {
        SemaphoreSlim fileLock = GetLock(ticketId);
        fileLock.Wait();
        try
        {
            string path = GetPath(ticketId);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            fileLock.Release();
        }
    }

    private string GetPath(string ticketId)
    {
        return Path.Combine(_directory, $"convos-{ticketId}.json");
    }

    private Dictionary<string, ConversationData> LoadFile(string ticketId)
    {
        string path = GetPath(ticketId);

        if (!File.Exists(path))
        {
            return new Dictionary<string, ConversationData>();
        }

        try
        {
            string json = File.ReadAllText(path);
            Dictionary<string, ConversationData>? data = JsonSerializer.Deserialize<Dictionary<string, ConversationData>>(json, ReadOptions);
            return data ?? new Dictionary<string, ConversationData>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load conversations from {Path}: {Error}", path, ex.Message);
            return new Dictionary<string, ConversationData>();
        }
    }

    private async Task SaveFileAsync(string ticketId, Dictionary<string, ConversationData> convos)
    {
        string path = GetPath(ticketId);
        string json = JsonSerializer.Serialize(convos, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }
}
