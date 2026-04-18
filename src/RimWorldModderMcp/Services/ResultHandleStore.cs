using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace RimWorldModderMcp.Services;

public sealed class ResultHandleStore
{
    private sealed record StoredResult(string ToolName, JsonNode Payload, DateTimeOffset CreatedAtUtc, int SizeBytes);

    private readonly ConcurrentDictionary<string, StoredResult> _store = new(StringComparer.OrdinalIgnoreCase);

    public string Store(string toolName, JsonNode payload)
    {
        var handle = $"res_{Guid.NewGuid():N}"[..16];
        var snapshot = ParseClone(payload);
        var sizeBytes = snapshot.ToJsonString().Length;

        _store[handle] = new StoredResult(toolName, snapshot, DateTimeOffset.UtcNow, sizeBytes);
        PruneIfNeeded();
        return handle;
    }

    public JsonNode? GetPayload(string handle)
    {
        return _store.TryGetValue(handle, out var stored)
            ? ParseClone(stored.Payload)
            : null;
    }

    public object? GetMetadata(string handle)
    {
        return _store.TryGetValue(handle, out var stored)
            ? new
            {
                handle,
                stored.ToolName,
                createdAtUtc = stored.CreatedAtUtc,
                stored.SizeBytes
            }
            : null;
    }

    private void PruneIfNeeded()
    {
        const int maxEntries = 200;
        if (_store.Count <= maxEntries)
        {
            return;
        }

        var oldest = _store.OrderBy(pair => pair.Value.CreatedAtUtc).Take(_store.Count - maxEntries).ToList();
        foreach (var entry in oldest)
        {
            _store.TryRemove(entry.Key, out _);
        }
    }

    private static JsonNode ParseClone(JsonNode node) =>
        JsonNode.Parse(node.ToJsonString()) ?? new JsonObject();
}
