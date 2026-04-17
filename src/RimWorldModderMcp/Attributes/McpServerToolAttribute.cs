using System;

namespace RimWorldModderMcp.Attributes;

/// <summary>
/// Marks a method as an MCP server tool that can be called by MCP clients.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public class McpServerToolAttribute : Attribute
{
    public McpServerToolAttribute()
    {
    }
}