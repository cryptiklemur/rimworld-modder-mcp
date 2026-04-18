namespace RimWorldModderMcp.Models;

public enum ToolOutputMode
{
    Compact,
    Normal,
    Detailed
}

public sealed class ToolExecutionOptions
{
    public ToolOutputMode OutputMode { get; init; } = ToolOutputMode.Normal;
    public int PageSize { get; init; } = 25;
    public int PageOffset { get; init; }
    public bool HandleResults { get; init; }
}
