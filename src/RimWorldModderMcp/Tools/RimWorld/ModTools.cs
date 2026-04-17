using System.Text.Json;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class ModTools
{
    [McpServerTool, Description("Get a list of all loaded RimWorld mods.")]
    public static string GetModList(ServerData serverData)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var mods = serverData.Mods.Values
            .OrderBy(m => m.LoadOrder)
            .Select(m => new
            {
                packageId = m.PackageId,
                name = m.Name,
                loadOrder = m.LoadOrder,
                author = m.Author,
                supportedVersions = m.SupportedVersions,
                path = m.Path,
                isCore = m.IsCore,
                defCount = serverData.Defs.Values.Count(d => d.Mod.PackageId == m.PackageId)
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            totalMods = mods.Count,
            mods
        });
    }
}