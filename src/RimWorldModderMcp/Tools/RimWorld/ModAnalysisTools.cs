using System.Text.Json;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class ModAnalysisTools
{
    [McpServerTool, Description("Use when you want a pairwise compatibility check between two specific mods.")]
    public static string AnalyzeModCompatibility(
        ServerData serverData,
        [Description("First mod package ID")] string modPackageId1,
        [Description("Second mod package ID")] string modPackageId2)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var mod1 = serverData.Mods.GetValueOrDefault(modPackageId1);
        var mod2 = serverData.Mods.GetValueOrDefault(modPackageId2);

        if (mod1 == null) return JsonSerializer.Serialize(new { error = $"Mod '{modPackageId1}' not found" });
        if (mod2 == null) return JsonSerializer.Serialize(new { error = $"Mod '{modPackageId2}' not found" });

        var analysis = new
        {
            mod1 = new { packageId = mod1.PackageId, name = mod1.Name, loadOrder = mod1.LoadOrder },
            mod2 = new { packageId = mod2.PackageId, name = mod2.Name, loadOrder = mod2.LoadOrder },
            compatibilityScore = CalculateCompatibilityScore(serverData, mod1, mod2),
            sharedDefinitions = GetSharedDefinitions(serverData, mod1, mod2),
            potentialConflicts = GetPotentialConflicts(serverData, mod1, mod2),
            loadOrderRecommendation = GetLoadOrderRecommendation(mod1, mod2),
            analysis = GetDetailedCompatibilityAnalysis(serverData, mod1, mod2)
        };

        return JsonSerializer.Serialize(analysis);
    }

    [McpServerTool, Description("Use when you need dependency, incompatibility, and ordering requirements for one mod.")]
    public static string GetModDependencies(
        ServerData serverData,
        [Description("Mod package ID to analyze")] string modPackageId)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var mod = serverData.Mods.GetValueOrDefault(modPackageId);
        if (mod == null) return JsonSerializer.Serialize(new { error = $"Mod '{modPackageId}' not found" });

        var dependencies = new
        {
            mod = new { packageId = mod.PackageId, name = mod.Name, loadOrder = mod.LoadOrder },
            requiredMods = GetRequiredMods(serverData, mod),
            optionalMods = GetOptionalMods(serverData, mod),
            dependentMods = GetDependentMods(serverData, mod),
            loadOrderRequirements = GetLoadOrderRequirements(serverData, mod),
            missingDependencies = GetMissingDependencies(serverData, mod)
        };

        return JsonSerializer.Serialize(dependencies);
    }

    [McpServerTool, Description("Use when you want unresolved DefName references across one mod or the whole loadout.")]
    public static string FindBrokenReferences(
        ServerData serverData,
        [Description("Optional: specific mod package ID to check")] string? modPackageId = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var brokenReferences = new List<object>();
        var modsToCheck = string.IsNullOrEmpty(modPackageId) 
            ? serverData.Mods.Values 
            : serverData.Mods.Values.Where(m => m.PackageId == modPackageId);

        foreach (var mod in modsToCheck)
        {
            var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod.PackageId);
            
            foreach (var def in modDefs)
            {
                var references = ExtractReferences(def.Content.ToString());
                
                foreach (var reference in references)
                {
                    if (!serverData.Defs.ContainsKey(reference))
                    {
                        brokenReferences.Add(new
                        {
                            mod = new { packageId = mod.PackageId, name = mod.Name },
                            defName = def.DefName,
                            brokenReference = reference,
                            context = GetReferenceContext(def.Content.ToString(), reference)
                        });
                    }
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            checkedMods = modsToCheck.Count(),
            totalBrokenReferences = brokenReferences.Count,
            brokenReferences = brokenReferences.Take(100).ToList()
        });
    }

    [McpServerTool, Description("Use when you want to verify a mod's folders and files match common RimWorld structure.")]
    public static string ValidateModStructure(
        ServerData serverData,
        [Description("Mod package ID to validate")] string modPackageId)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var mod = serverData.Mods.GetValueOrDefault(modPackageId);
        if (mod == null) return JsonSerializer.Serialize(new { error = $"Mod '{modPackageId}' not found" });

        var validation = new
        {
            mod = new { packageId = mod.PackageId, name = mod.Name, path = mod.Path },
            structureChecks = ValidateDirectoryStructure(mod),
            fileChecks = ValidateRequiredFiles(mod),
            defChecks = ValidateDefinitionFiles(serverData, mod),
            overallScore = 0, // Will be calculated
            recommendations = GenerateStructureRecommendations(mod)
        };

        return JsonSerializer.Serialize(validation);
    }

    private static int CalculateCompatibilityScore(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        var conflicts = GetPotentialConflicts(serverData, mod1, mod2);
        var shared = GetSharedDefinitions(serverData, mod1, mod2);
        
        var baseScore = 100;
        var conflictPenalty = conflicts.Count() * 10;
        var sharedBonus = Math.Min(shared.Count() * 2, 20);
        
        return Math.Max(0, baseScore - conflictPenalty + sharedBonus);
    }

    private static IEnumerable<object> GetSharedDefinitions(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        var mod1Defs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod1.PackageId).Select(d => d.DefName).ToHashSet();
        var mod2Defs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod2.PackageId).Select(d => d.DefName).ToHashSet();
        
        return mod1Defs.Intersect(mod2Defs).Select(defName => new { defName });
    }

    private static IEnumerable<object> GetPotentialConflicts(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        var conflicts = serverData.Conflicts.Where(c => 
            c.Mods.Any(m => m.PackageId == mod1.PackageId) && 
            c.Mods.Any(m => m.PackageId == mod2.PackageId));

        return conflicts.Select(c => new
        {
            type = c.Type.ToString(),
            severity = c.Severity.ToString(),
            description = c.Description,
            defName = c.DefName
        });
    }

    private static string GetLoadOrderRecommendation(ModInfo mod1, ModInfo mod2)
    {
        if (mod1.IsCore) return $"{mod1.Name} should load before {mod2.Name}";
        if (mod2.IsCore) return $"{mod2.Name} should load before {mod1.Name}";
        
        return mod1.LoadOrder < mod2.LoadOrder 
            ? $"Current order ({mod1.Name} -> {mod2.Name}) appears correct"
            : $"Consider reversing order: {mod2.Name} -> {mod1.Name}";
    }

    private static object GetDetailedCompatibilityAnalysis(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        return new
        {
            xmlPatches = GetXmlPatchInteractions(serverData, mod1, mod2),
            defOverrides = GetDefinitionOverrides(serverData, mod1, mod2),
            loadOrderIssues = GetLoadOrderIssues(mod1, mod2),
            recommendedActions = GetRecommendedActions(serverData, mod1, mod2)
        };
    }

    private static List<object> GetRequiredMods(ServerData serverData, ModInfo mod)
    {
        // This would need to parse ModsConfig or dependency information
        return new List<object>();
    }

    private static List<object> GetOptionalMods(ServerData serverData, ModInfo mod)
    {
        return new List<object>();
    }

    private static List<object> GetDependentMods(ServerData serverData, ModInfo mod)
    {
        return new List<object>();
    }

    private static List<object> GetLoadOrderRequirements(ServerData serverData, ModInfo mod)
    {
        return new List<object>
        {
            new { requirement = "Load after Core", satisfied = mod.LoadOrder > 0 }
        };
    }

    private static List<object> GetMissingDependencies(ServerData serverData, ModInfo mod)
    {
        return new List<object>();
    }

    private static List<string> ExtractReferences(string xmlContent)
    {
        var references = new List<string>();
        
        // Extract common reference patterns from XML
        var patterns = new[]
        {
            @"<li>([A-Za-z_][A-Za-z0-9_]*)</li>",
            @"<defName>([A-Za-z_][A-Za-z0-9_]*)</defName>",
            @"ParentName=""([A-Za-z_][A-Za-z0-9_]*)"""
        };

        foreach (var pattern in patterns)
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(xmlContent, pattern);
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (match.Groups.Count > 1)
                {
                    references.Add(match.Groups[1].Value);
                }
            }
        }

        return references.Distinct().ToList();
    }

    private static string GetReferenceContext(string xmlContent, string reference)
    {
        var index = xmlContent.IndexOf(reference, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = Math.Max(0, index - 30);
            var length = Math.Min(xmlContent.Length - start, 80);
            var context = xmlContent.Substring(start, length).Replace("\\n", " ");
            return "[...]" + context + "[...]";
        }
        return string.Empty;
    }

    private static List<object> ValidateDirectoryStructure(ModInfo mod)
    {
        return new List<object>
        {
            new { check = "Has About folder", passed = Directory.Exists(Path.Combine(mod.Path, "About")) },
            new { check = "Has Defs folder", passed = Directory.Exists(Path.Combine(mod.Path, "Defs")) }
        };
    }

    private static List<object> ValidateRequiredFiles(ModInfo mod)
    {
        return new List<object>
        {
            new { file = "About/About.xml", exists = File.Exists(Path.Combine(mod.Path, "About", "About.xml")) }
        };
    }

    private static List<object> ValidateDefinitionFiles(ServerData serverData, ModInfo mod)
    {
        var defCount = serverData.Defs.Values.Count(d => d.Mod.PackageId == mod.PackageId);
        return new List<object>
        {
            new { check = "Has definitions", passed = defCount > 0, count = defCount }
        };
    }

    private static List<string> GenerateStructureRecommendations(ModInfo mod)
    {
        return new List<string>
        {
            "Ensure About.xml contains proper metadata",
            "Organize definitions in appropriate subfolders",
            "Include proper version information"
        };
    }

    private static List<object> GetXmlPatchInteractions(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        return new List<object>();
    }

    private static List<object> GetDefinitionOverrides(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        return new List<object>();
    }

    private static List<object> GetLoadOrderIssues(ModInfo mod1, ModInfo mod2)
    {
        return new List<object>();
    }

    private static List<string> GetRecommendedActions(ServerData serverData, ModInfo mod1, ModInfo mod2)
    {
        return new List<string>
        {
            "Test both mods together in a new save",
            "Check for definition conflicts",
            "Monitor for runtime errors"
        };
    }
}
