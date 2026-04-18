using System.Text.Json;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Services;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.Conflicts;

public static class ConflictTools
{
    [McpServerTool, Description("Use when you need stored conflict records filtered by mod, conflict type, or severity.")]
    public static string GetConflicts(
        ServerData serverData,
        ConflictDetector conflictDetector,
        [Description("Optional: filter by mod package ID")] string? modPackageId = null, 
        [Description("Optional: filter by conflict type (Override, Incompatible, etc.)")] string? conflictType = null, 
        [Description("Optional: filter by severity (Low, Medium, High, Critical)")] string? severity = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });
        if (conflictDetector == null) return JsonSerializer.Serialize(new { error = "Conflict detector not available" });

        var conflicts = serverData.Conflicts.ToList();

        // Filter by mod if specified
        if (!string.IsNullOrEmpty(modPackageId))
        {
            conflicts = conflictDetector.GetConflictsByMod(serverData, modPackageId);
        }

        // Filter by conflict type if specified
        if (!string.IsNullOrEmpty(conflictType) && Enum.TryParse<ConflictType>(conflictType, true, out var type))
        {
            conflicts = conflicts.Where(c => c.Type == type).ToList();
        }

        // Filter by severity if specified
        if (!string.IsNullOrEmpty(severity) && Enum.TryParse<ConflictSeverity>(severity, true, out var sev))
        {
            conflicts = conflicts.Where(c => c.Severity == sev).ToList();
        }

        var formattedConflicts = conflicts.Select(conflict => new
        {
            type = conflict.Type.ToString(),
            severity = conflict.Severity.ToString(),
            defName = conflict.DefName,
            xpath = conflict.XPath,
            description = conflict.Description,
            resolution = conflict.Resolution,
            mods = conflict.Mods.Select(m => new
            {
                packageId = m.PackageId,
                name = m.Name,
                loadOrder = m.LoadOrder
            }).ToList()
        }).ToList<object>();

        return JsonSerializer.Serialize(new
        {
            modPackageId,
            conflictType,
            severity,
            totalConflicts = formattedConflicts.Count,
            conflicts = formattedConflicts
        });
    }
}
