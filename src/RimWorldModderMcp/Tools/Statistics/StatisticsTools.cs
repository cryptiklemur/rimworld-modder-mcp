using System.Text.Json;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.Statistics;

public static class StatisticsTools
{
    [McpServerTool, Description("Use when you need a fast overview of loaded mods, defs, patches, and load status before deeper analysis.")]
    public static string GetStatistics(ServerData serverData)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        // Calculate loading status
        var loadingTimeElapsed = (DateTime.UtcNow - serverData.StartTime).TotalSeconds;
        var isFullyLoaded = serverData.IsFullyLoaded;
        
        string statusMessage = isFullyLoaded ? "✅ Server fully loaded and operational" :
                              serverData.IsModsLoaded && serverData.IsDefsLoaded ? "🔄 Server loading in background..." :
                              "⏳ Server initializing...";

        // Def type counts
        var defTypeCounts = serverData.Defs.Values
            .GroupBy(d => d.Type)
            .ToDictionary(g => g.Key, g => g.Count())
            .OrderByDescending(kvp => kvp.Value)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        // Top mods by def count
        var topMods = serverData.Mods.Values
            .Select(m => new
            {
                packageId = m.PackageId,
                name = m.Name,
                defCount = serverData.Defs.Values.Count(d => d.Mod.PackageId == m.PackageId)
            })
            .OrderByDescending(m => m.defCount)
            .Take(10)
            .ToList();

        // Abstract def count
        var abstractDefCount = serverData.Defs.Values.Count(d => d.Abstract);

        return JsonSerializer.Serialize(new
        {
            loadingStatus = new
            {
                isFullyLoaded,
                isModsLoaded = serverData.IsModsLoaded,
                isDefsLoaded = serverData.IsDefsLoaded,
                isConflictsAnalyzed = serverData.IsConflictsAnalyzed,
                loadingTimeElapsed,
                message = statusMessage
            },
            totalMods = serverData.Mods.Count,
            totalDefs = serverData.Defs.Count,
            totalAbstractDefs = abstractDefCount,
            totalConflicts = serverData.Conflicts.Count,
            defTypeCounts,
            topModsByDefCount = topMods
        });
    }
}
