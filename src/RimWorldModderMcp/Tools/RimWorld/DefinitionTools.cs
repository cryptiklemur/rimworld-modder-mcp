using System.Text.Json;
using System.Xml.Linq;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class DefinitionTools
{
    [McpServerTool, Description("Use when you know the exact defName and want the loaded definition payload.")]
    public static string GetDef(
        ServerData serverData,
        [Description("The name of the definition to retrieve")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });
        
        var def = serverData.Defs.GetValueOrDefault(defName);
        if (def == null)
        {
            return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });
        }

        return JsonSerializer.Serialize(new
        {
            defName = def.DefName,
            type = def.Type,
            parent = def.Parent,
            @abstract = def.Abstract,
            content = def.Content.ToString(),
            mod = new
            {
                packageId = def.Mod.PackageId,
                name = def.Mod.Name,
                loadOrder = def.Mod.LoadOrder
            },
            filePath = def.FilePath,
            conflicts = def.Conflicts.Select(c => new
            {
                type = c.Type.ToString(),
                severity = c.Severity.ToString(),
                description = c.Description
            })
        });
    }

    [McpServerTool, Description("Use when you need a list of loaded definitions for one def type.")]
    public static string GetDefsByType(
        ServerData serverData,
        [Description("The type of definitions to retrieve (e.g., ThingDef, RecipeDef)")] string type)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var defs = serverData.Defs.Values
            .Where(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase))
            .Select(d => new
            {
                defName = d.DefName,
                parent = d.Parent,
                @abstract = d.Abstract,
                mod = d.Mod.Name
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            type,
            count = defs.Count,
            defs
        });
    }

    [McpServerTool, Description("Use when you do not know the exact defName and need to search loaded definitions by term.")]
    public static string SearchDefs(
        ServerData serverData,
        [Description("The search term to look for in definition names or content")] string searchTerm,
        [Description("Optional: filter results to a specific type (e.g., ThingDef)")] string? inType = null,
        [Description("Maximum number of results to return")] int maxResults = 100)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var query = serverData.Defs.Values.AsEnumerable();

        // Filter by type if specified
        if (!string.IsNullOrEmpty(inType))
        {
            query = query.Where(d => d.Type.Equals(inType, StringComparison.OrdinalIgnoreCase));
        }

        // Search in def names and content
        var results = query
            .Where(d => d.DefName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                       d.Content.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .Select(d => new
            {
                defName = d.DefName,
                type = d.Type,
                parent = d.Parent,
                @abstract = d.Abstract,
                mod = new
                {
                    packageId = d.Mod.PackageId,
                    name = d.Mod.Name
                },
                matchContext = GetMatchContext(d, searchTerm)
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            searchTerm,
            inType,
            count = results.Count,
            results
        });
    }

    private static string GetMatchContext(RimWorldDef def, string searchTerm)
    {
        var content = def.Content.ToString();
        var index = content.IndexOf(searchTerm, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = Math.Max(0, index - 50);
            var length = Math.Min(content.Length - start, 100 + searchTerm.Length);
            return "..." + content.Substring(start, length) + "...";
        }
        return string.Empty;
    }

    [McpServerTool, Description("Use when you need parent and abstract ancestry for one definition.")]
    public static string GetDefInheritanceTree(
        ServerData serverData,
        [Description("The name of the definition to analyze")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var def = serverData.Defs.GetValueOrDefault(defName);
        if (def == null)
        {
            return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });
        }

        var inheritanceChain = new List<object>();
        var current = def;
        var visited = new HashSet<string>();

        while (current != null && !visited.Contains(current.DefName))
        {
            visited.Add(current.DefName);
            
            inheritanceChain.Add(new
            {
                defName = current.DefName,
                type = current.Type,
                isAbstract = current.Abstract,
                mod = new { packageId = current.Mod.PackageId, name = current.Mod.Name },
                level = inheritanceChain.Count
            });

            if (!string.IsNullOrEmpty(current.Parent))
            {
                current = serverData.Defs.GetValueOrDefault(current.Parent);
            }
            else
            {
                break;
            }
        }

        inheritanceChain.Reverse();

        return JsonSerializer.Serialize(new
        {
            defName = defName,
            inheritanceChain = inheritanceChain,
            totalLevels = inheritanceChain.Count
        });
    }

    [McpServerTool, Description("Use when you want every XML patch that touches a specific definition.")]
    public static string GetPatchesForDef(
        ServerData serverData,
        [Description("The name of the definition to find patches for")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var patches = new List<object>();

        foreach (var patch in serverData.GlobalPatches)
        {
            if (patch.XPath?.Contains(defName, StringComparison.OrdinalIgnoreCase) == true ||
                patch.Value?.ToString().Contains(defName, StringComparison.OrdinalIgnoreCase) == true)
            {
                patches.Add(new
                {
                    xpath = patch.XPath,
                    operation = patch.Operation.ToString(),
                    value = patch.Value?.ToString(),
                    mod = new
                    {
                        packageId = patch.Mod.PackageId,
                        name = patch.Mod.Name,
                        loadOrder = patch.Mod.LoadOrder
                    },
                    filePath = patch.FilePath
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            defName = defName,
            totalPatches = patches.Count,
            patches = patches.Take(50).ToList()
        });
    }

    [McpServerTool, Description("Use when you want a side-by-side difference between two loaded definitions.")]
    public static string CompareDefs(
        ServerData serverData,
        [Description("First definition name")] string defName1,
        [Description("Second definition name")] string defName2)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var def1 = serverData.Defs.GetValueOrDefault(defName1);
        var def2 = serverData.Defs.GetValueOrDefault(defName2);

        if (def1 == null) return JsonSerializer.Serialize(new { error = $"Definition '{defName1}' not found" });
        if (def2 == null) return JsonSerializer.Serialize(new { error = $"Definition '{defName2}' not found" });

        var differences = new List<object>();

        // Compare basic properties
        if (def1.Type != def2.Type)
            differences.Add(new { property = "Type", value1 = def1.Type, value2 = def2.Type });

        if (def1.Parent != def2.Parent)
            differences.Add(new { property = "Parent", value1 = def1.Parent, value2 = def2.Parent });

        if (def1.Abstract != def2.Abstract)
            differences.Add(new { property = "Abstract", value1 = def1.Abstract, value2 = def2.Abstract });

        // Compare XML content structure
        var xmlDifferences = CompareXmlContent(def1.Content.ToString(), def2.Content.ToString());
        differences.AddRange(xmlDifferences);

        return JsonSerializer.Serialize(new
        {
            def1 = new { defName = def1.DefName, mod = def1.Mod.Name },
            def2 = new { defName = def2.DefName, mod = def2.Mod.Name },
            totalDifferences = differences.Count,
            differences = differences.Take(20).ToList()
        });
    }

    [McpServerTool, Description("Use when you want abstract defs that can serve as inheritance bases.")]
    public static string GetAbstractDefs(
        ServerData serverData,
        [Description("Optional: filter by definition type")] string? type = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var query = serverData.Defs.Values.Where(d => d.Abstract);

        if (!string.IsNullOrEmpty(type))
        {
            query = query.Where(d => d.Type.Equals(type, StringComparison.OrdinalIgnoreCase));
        }

        var abstractDefs = query
            .GroupBy(d => d.Type)
            .ToDictionary(g => g.Key, g => g.Select(d => new
            {
                defName = d.DefName,
                mod = new { packageId = d.Mod.PackageId, name = d.Mod.Name },
                childCount = serverData.Defs.Values.Count(child => child.Parent == d.DefName)
            }).OrderBy(d => d.defName).ToList());

        return JsonSerializer.Serialize(new
        {
            filterType = type,
            totalTypes = abstractDefs.Count,
            totalAbstractDefs = abstractDefs.Values.Sum(list => list.Count),
            abstractDefsByType = abstractDefs
        });
    }

    private static List<object> CompareXmlContent(string xml1, string xml2)
    {
        var differences = new List<object>();
        
        try
        {
            var doc1 = XDocument.Parse(xml1);
            var doc2 = XDocument.Parse(xml2);

            // Find elements in doc1 not in doc2
            var elements1 = doc1.Descendants().ToDictionary(e => e.Name.LocalName + ":" + e.Value, e => e);
            var elements2 = doc2.Descendants().ToDictionary(e => e.Name.LocalName + ":" + e.Value, e => e);

            foreach (var kvp in elements1)
            {
                if (!elements2.ContainsKey(kvp.Key))
                {
                    differences.Add(new
                    {
                        type = "missing_in_def2",
                        element = kvp.Value.Name.LocalName,
                        value = kvp.Value.Value
                    });
                }
            }

            foreach (var kvp in elements2)
            {
                if (!elements1.ContainsKey(kvp.Key))
                {
                    differences.Add(new
                    {
                        type = "missing_in_def1",
                        element = kvp.Value.Name.LocalName,
                        value = kvp.Value.Value
                    });
                }
            }
        }
        catch (Exception ex)
        {
            differences.Add(new { type = "xml_parse_error", message = ex.Message });
        }

        return differences;
    }
}
