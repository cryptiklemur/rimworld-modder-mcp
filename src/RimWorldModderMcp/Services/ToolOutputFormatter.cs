using System.Text.Json;
using System.Text.Json.Nodes;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public static class ToolOutputFormatter
{
    private sealed record OutputProfile(int MaxItemsPerArray, int MaxStringLength, int MaxDepth);
    private sealed record PaginationEntry(string Path, int Total, int Returned, int Offset);

    public static ToolExecutionResult Format(
        string toolName,
        JsonNode rawNode,
        ToolExecutionOptions options,
        ResultHandleStore handleStore)
    {
        var profile = GetProfile(options.OutputMode);
        var pagination = new List<PaginationEntry>();
        var truncatedPaths = new List<string>();
        var transformed = TransformNode(ParseClone(rawNode), "$", 0, profile, options, pagination, truncatedPaths);

        var rawJson = rawNode.ToJsonString();
        string? handle = null;
        if (options.HandleResults || rawJson.Length > 12000 || pagination.Count > 0 || truncatedPaths.Count > 0)
        {
            handle = handleStore.Store(toolName, rawNode);
        }

        var withMeta = AttachMeta(toolName, transformed, options, handle, pagination, truncatedPaths);
        var text = withMeta.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

        return new ToolExecutionResult
        {
            Text = text,
            Structured = withMeta,
            Handle = handle
        };
    }

    public static JsonNode ParseNode(object? result)
    {
        if (result == null)
        {
            return new JsonObject();
        }

        if (result is JsonNode node)
        {
            return ParseClone(node);
        }

        if (result is string stringResult)
        {
            try
            {
                return JsonNode.Parse(stringResult) ?? JsonValue.Create(stringResult)!;
            }
            catch (JsonException)
            {
                return JsonValue.Create(stringResult)!;
            }
        }

        return JsonSerializer.SerializeToNode(result) ?? new JsonObject();
    }

    public static ToolOutputMode ParseMode(string? rawMode) =>
        rawMode?.Trim().ToLowerInvariant() switch
        {
            "compact" => ToolOutputMode.Compact,
            "detailed" => ToolOutputMode.Detailed,
            _ => ToolOutputMode.Normal
        };

    private static JsonNode AttachMeta(
        string toolName,
        JsonNode node,
        ToolExecutionOptions options,
        string? handle,
        IReadOnlyCollection<PaginationEntry> pagination,
        IReadOnlyCollection<string> truncatedPaths)
    {
        JsonObject root;
        if (node is JsonObject objectNode)
        {
            root = objectNode;
        }
        else
        {
            root = new JsonObject
            {
                ["value"] = node
            };
        }

        var meta = new JsonObject
        {
            ["tool"] = toolName,
            ["outputMode"] = options.OutputMode.ToString().ToLowerInvariant(),
            ["pageSize"] = options.PageSize,
            ["pageOffset"] = options.PageOffset
        };

        if (!string.IsNullOrWhiteSpace(handle))
        {
            meta["resultHandle"] = handle;
        }

        if (pagination.Count > 0)
        {
            meta["pagination"] = new JsonArray(pagination.Select(entry => new JsonObject
            {
                ["path"] = entry.Path,
                ["total"] = entry.Total,
                ["returned"] = entry.Returned,
                ["offset"] = entry.Offset
            }).ToArray());
        }

        if (truncatedPaths.Count > 0)
        {
            meta["truncatedPaths"] = new JsonArray(
                truncatedPaths
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .Select(path => JsonValue.Create(path))
                    .ToArray());
        }

        root["_meta"] = meta;
        return root;
    }

    private static JsonNode TransformNode(
        JsonNode node,
        string path,
        int depth,
        OutputProfile profile,
        ToolExecutionOptions options,
        ICollection<PaginationEntry> pagination,
        ICollection<string> truncatedPaths)
    {
        if (depth >= profile.MaxDepth)
        {
            if (node is JsonArray arrayNode)
            {
                truncatedPaths.Add(path);
                return new JsonObject
                {
                    ["_truncated"] = "depth",
                    ["type"] = "array",
                    ["count"] = arrayNode.Count
                };
            }

            if (node is JsonObject objectNode)
            {
                truncatedPaths.Add(path);
                return new JsonObject
                {
                    ["_truncated"] = "depth",
                    ["type"] = "object",
                    ["propertyCount"] = objectNode.Count
                };
            }

            if (node is JsonValue valueNode)
            {
                return TransformValue(valueNode, path, profile, truncatedPaths);
            }

            return ParseClone(node);
        }

        return node switch
        {
            JsonObject objectNode => TransformObject(objectNode, path, depth, profile, options, pagination, truncatedPaths),
            JsonArray arrayNode => TransformArray(arrayNode, path, depth, profile, options, pagination, truncatedPaths),
            JsonValue valueNode => TransformValue(valueNode, path, profile, truncatedPaths),
            _ => node
        };
    }

    private static JsonObject TransformObject(
        JsonObject objectNode,
        string path,
        int depth,
        OutputProfile profile,
        ToolExecutionOptions options,
        ICollection<PaginationEntry> pagination,
        ICollection<string> truncatedPaths)
    {
        var transformed = new JsonObject();
        foreach (var property in objectNode)
        {
            transformed[property.Key] = property.Value == null
                ? null
                : TransformNode(property.Value, $"{path}.{property.Key}", depth + 1, profile, options, pagination, truncatedPaths);
        }

        return transformed;
    }

    private static JsonArray TransformArray(
        JsonArray arrayNode,
        string path,
        int depth,
        OutputProfile profile,
        ToolExecutionOptions options,
        ICollection<PaginationEntry> pagination,
        ICollection<string> truncatedPaths)
    {
        var total = arrayNode.Count;
        var offset = Math.Max(0, options.PageOffset);
        var effectivePageSize = options.PageSize > 0 ? options.PageSize : profile.MaxItemsPerArray;
        var effectiveLimit = Math.Max(1, Math.Min(effectivePageSize, profile.MaxItemsPerArray));
        var slice = arrayNode.Skip(offset).Take(effectiveLimit).ToList();

        if (offset > 0 || total > effectiveLimit)
        {
            pagination.Add(new PaginationEntry(path, total, slice.Count, offset));
            truncatedPaths.Add(path);
        }

        var transformed = new JsonArray();
        foreach (var item in slice)
        {
            transformed.Add(item == null
                ? null
                : TransformNode(item, $"{path}[]", depth + 1, profile, options, pagination, truncatedPaths));
        }

        return transformed;
    }

    private static JsonNode TransformValue(
        JsonValue valueNode,
        string path,
        OutputProfile profile,
        ICollection<string> truncatedPaths)
    {
        if (valueNode.TryGetValue<string>(out var stringValue) && stringValue.Length > profile.MaxStringLength)
        {
            truncatedPaths.Add(path);
            return JsonValue.Create($"{stringValue[..profile.MaxStringLength]}...");
        }

        return ParseClone(valueNode);
    }

    private static OutputProfile GetProfile(ToolOutputMode mode) => mode switch
    {
        ToolOutputMode.Compact => new OutputProfile(10, 220, 4),
        ToolOutputMode.Detailed => new OutputProfile(100, 4000, 10),
        _ => new OutputProfile(25, 900, 6)
    };

    private static JsonNode ParseClone(JsonNode node) =>
        JsonNode.Parse(node.ToJsonString()) ?? new JsonObject();
}
