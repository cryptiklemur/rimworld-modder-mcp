using System.Text.Json;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class ReferenceTools
{
    [McpServerTool, Description("Find all references to a specific definition (what uses this item/pawn/etc.).")]
    public static string GetReferences(
        ServerData serverData,
        [Description("The name of the definition to find references for")] string defName,
        [Description("Include references from inactive mods")] bool includeInactive = false)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var references = new List<object>();
        
        foreach (var kvp in serverData.Defs)
        {
            var def = kvp.Value;
            
            // Skip inactive mods unless requested - for now, we'll include all mods
            // TODO: Implement ModsConfig.xml parsing to determine active/inactive status

            // Check if this def references the target defName in its content
            var contentStr = def.Content.ToString();
            if (contentStr.Contains(defName, StringComparison.OrdinalIgnoreCase))
            {
                references.Add(new
                {
                    defName = def.DefName,
                    type = def.Type,
                    mod = new
                    {
                        packageId = def.Mod.PackageId,
                        name = def.Mod.Name,
                        isActive = true // TODO: Determine from ModsConfig.xml
                    },
                    context = ExtractContext(contentStr, defName)
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            searchedFor = defName,
            totalReferences = references.Count,
            references = references.Take(100).ToList() // Limit results
        });
    }

    private static string ExtractContext(string content, string searchTerm)
    {
        var index = content.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = Math.Max(0, index - 50);
            var length = Math.Min(content.Length - start, 150);
            var context = content.Substring(start, length);
            
            // Clean up the context for better readability
            context = context.Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (start > 0) context = "..." + context;
            if (start + length < content.Length) context = context + "...";
            
            return context;
        }
        return string.Empty;
    }
}