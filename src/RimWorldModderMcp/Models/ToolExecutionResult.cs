using System.Text.Json.Nodes;

namespace RimWorldModderMcp.Models;

public sealed class ToolExecutionResult
{
    public required string Text { get; init; }
    public required JsonNode Structured { get; init; }
    public string? Handle { get; init; }
}
