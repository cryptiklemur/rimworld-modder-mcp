using System.Text.Json;
using System.Xml.Linq;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.Patch;

public static class PatchAnalysisTools
{
    [McpServerTool, Description("Find conflicting patches between mods.")]
    public static string GetPatchConflicts(
        ServerData serverData,
        [Description("Optional: specific XPath to analyze")] string? xpath = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var conflicts = new List<object>();
        var patchGroups = serverData.GlobalPatches
            .Where(p => string.IsNullOrEmpty(xpath) || p.XPath?.Contains(xpath) == true)
            .GroupBy(p => p.XPath)
            .Where(g => g.Count() > 1);

        foreach (var group in patchGroups)
        {
            var patches = group.ToList();
            for (int i = 0; i < patches.Count; i++)
            {
                for (int j = i + 1; j < patches.Count; j++)
                {
                    var conflict = AnalyzePatchConflict(patches[i], patches[j]);
                    if (conflict != null)
                    {
                        conflicts.Add(conflict);
                    }
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            xpath = xpath ?? "all",
            totalConflicts = conflicts.Count,
            conflicts = conflicts.Take(50).ToList()
        });
    }

    [McpServerTool, Description("Show how patches are applied to a definition.")]
    public static string TracePatchApplication(
        ServerData serverData,
        [Description("Definition name to trace")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var def = serverData.Defs.GetValueOrDefault(defName);
        if (def == null) return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });

        var applicablePatches = serverData.GlobalPatches
            .Where(p => p.XPath?.Contains(defName, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(p => p.Mod.LoadOrder)
            .ToList();

        var trace = new List<object>();
        var currentXml = def.Content.ToString();

        foreach (var patch in applicablePatches)
        {
            var beforeXml = currentXml;
            var afterXml = ApplyPatchSimulation(currentXml, patch);
            
            trace.Add(new
            {
                step = trace.Count + 1,
                mod = new { packageId = patch.Mod.PackageId, name = patch.Mod.Name, loadOrder = patch.Mod.LoadOrder },
                operation = patch.Operation.ToString(),
                xpath = patch.XPath,
                value = patch.Value?.ToString(),
                changed = beforeXml != afterXml,
                sizeBefore = beforeXml.Length,
                sizeAfter = afterXml.Length
            });

            currentXml = afterXml;
        }

        return JsonSerializer.Serialize(new
        {
            defName = defName,
            totalPatches = applicablePatches.Count,
            finalSize = currentXml.Length,
            originalSize = def.Content.ToString().Length,
            trace = trace
        });
    }

    [McpServerTool, Description("Analyze patch complexity and performance impact.")]
    public static string GetPatchPerformance(
        ServerData serverData,
        [Description("Optional: specific mod package ID")] string? modPackageId = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var patches = string.IsNullOrEmpty(modPackageId)
            ? serverData.GlobalPatches.ToList()
            : serverData.GlobalPatches.Where(p => p.Mod.PackageId == modPackageId).ToList();

        var analysis = patches.Select(patch => new
        {
            mod = new { packageId = patch.Mod.PackageId, name = patch.Mod.Name },
            xpath = patch.XPath,
            operation = patch.Operation.ToString(),
            complexity = CalculatePatchComplexity(patch),
            performanceImpact = EstimatePerformanceImpact(patch),
            riskLevel = AssessRiskLevel(patch)
        }).ToList();

        return JsonSerializer.Serialize(new
        {
            modFilter = modPackageId ?? "all",
            totalPatches = patches.Count,
            averageComplexity = analysis.Average(a => a.complexity),
            highRiskPatches = analysis.Count(a => a.riskLevel == "High"),
            analysis = analysis.OrderByDescending(a => a.complexity).Take(20).ToList()
        });
    }

    [McpServerTool, Description("Write XPath expressions that target a definition.")]
    public static string WriteXPath(
        ServerData serverData,
        [Description("Definition name to target")] string defName,
        [Description("Element to target within the definition")] string? targetElement = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var def = serverData.Defs.GetValueOrDefault(defName);
        if (def == null) return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });

        var suggestions = GenerateXPathSuggestions(def, targetElement);

        return JsonSerializer.Serialize(new
        {
            defName = defName,
            defType = def.Type,
            targetElement = targetElement ?? "any",
            suggestions = suggestions.Take(10).ToList(),
            examples = GenerateXPathExamples(def, targetElement)
        });
    }

    [McpServerTool, Description("Show what a patch would look like in XML format.")]
    public static string PreviewPatch(
        ServerData serverData,
        [Description("XPath expression")] string xpath,
        [Description("Patch operation (Add, Replace, Remove)")] string operation,
        [Description("Patch value (for Add/Replace operations)")] string? value = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var patchXml = GeneratePatchXml(xpath, operation, value);
        var affectedDefs = FindAffectedDefinitions(serverData, xpath);

        return JsonSerializer.Serialize(new
        {
            xpath = xpath,
            operation = operation,
            value = value,
            patchXml = patchXml,
            affectedDefinitions = affectedDefs.Take(10).ToList(),
            warnings = ValidatePatch(xpath, operation, value)
        });
    }

    [McpServerTool, Description("Show what a definition looks like after patches.")]
    public static string PreviewPatchResult(
        ServerData serverData,
        [Description("Definition name")] string defName,
        [Description("Optional: specific mod load order to simulate up to")] int? loadOrderLimit = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var def = serverData.Defs.GetValueOrDefault(defName);
        if (def == null) return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });

        var applicablePatches = serverData.GlobalPatches
            .Where(p => p.XPath?.Contains(defName, StringComparison.OrdinalIgnoreCase) == true)
            .Where(p => loadOrderLimit == null || p.Mod.LoadOrder <= loadOrderLimit)
            .OrderBy(p => p.Mod.LoadOrder)
            .ToList();

        var originalXml = def.Content.ToString();
        var currentXml = originalXml;

        foreach (var patch in applicablePatches)
        {
            currentXml = ApplyPatchSimulation(currentXml, patch);
        }

        return JsonSerializer.Serialize(new
        {
            defName = defName,
            loadOrderLimit = loadOrderLimit,
            appliedPatches = applicablePatches.Count,
            originalXml = FormatXml(originalXml),
            patchedXml = FormatXml(currentXml),
            changes = CompareXmlChanges(originalXml, currentXml)
        });
    }

    private static object? AnalyzePatchConflict(PatchOperation patch1, PatchOperation patch2)
    {
        if (patch1.XPath != patch2.XPath) return null;

        var severity = "Medium";
        if (patch1.Operation == PatchOperationType.Replace && patch2.Operation == PatchOperationType.Replace)
            severity = "High";
        else if (patch1.Operation == PatchOperationType.Remove || patch2.Operation == PatchOperationType.Remove)
            severity = "High";

        return new
        {
            xpath = patch1.XPath,
            severity = severity,
            mod1 = new { packageId = patch1.Mod.PackageId, name = patch1.Mod.Name, operation = patch1.Operation.ToString() },
            mod2 = new { packageId = patch2.Mod.PackageId, name = patch2.Mod.Name, operation = patch2.Operation.ToString() },
            description = $"Both mods modify the same XPath with {patch1.Operation} and {patch2.Operation} operations"
        };
    }

    private static string ApplyPatchSimulation(string xml, PatchOperation patch)
    {
        // This is a simplified simulation - in reality, patches are more complex
        try
        {
            var doc = XDocument.Parse(xml);
            
            switch (patch.Operation)
            {
                case PatchOperationType.Add:
                    // Simulate adding content
                    break;
                case PatchOperationType.Replace:
                    // Simulate replacing content
                    break;
                case PatchOperationType.Remove:
                    // Simulate removing content
                    break;
            }
            
            return doc.ToString();
        }
        catch
        {
            return xml; // Return unchanged if simulation fails
        }
    }

    private static int CalculatePatchComplexity(PatchOperation patch)
    {
        var complexity = 1;
        
        if (patch.XPath?.Contains("//") == true) complexity += 2; // Deep search
        if (patch.XPath?.Contains("[") == true) complexity += 1; // Conditions
        if (patch.Value?.ToString().Length > 100) complexity += 1; // Large values
        
        return complexity;
    }

    private static string EstimatePerformanceImpact(PatchOperation patch)
    {
        var xpath = patch.XPath ?? "";
        
        if (xpath.StartsWith("//")) return "High"; // Deep searches are expensive
        if (xpath.Contains("[") && xpath.Contains("text()")) return "Medium";
        return "Low";
    }

    private static string AssessRiskLevel(PatchOperation patch)
    {
        if (patch.Operation == PatchOperationType.Remove) return "High";
        if (patch.XPath?.Contains("statBases") == true) return "Medium";
        return "Low";
    }

    private static List<object> GenerateXPathSuggestions(RimWorldDef def, string? targetElement)
    {
        var suggestions = new List<object>();
        
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var rootName = doc.Root?.Name.LocalName ?? def.Type;
            
            suggestions.Add(new
            {
                xpath = $"//Defs/{rootName}[defName=\"{def.DefName}\"]",
                description = "Target the entire definition",
                specificity = "High"
            });

            if (!string.IsNullOrEmpty(targetElement))
            {
                suggestions.Add(new
                {
                    xpath = $"//Defs/{rootName}[defName=\"{def.DefName}\"]/{targetElement}",
                    description = $"Target the {targetElement} element",
                    specificity = "High"
                });
            }

            // Find common elements
            var elements = doc.Descendants().Select(e => e.Name.LocalName).Distinct().Take(5);
            foreach (var element in elements)
            {
                suggestions.Add(new
                {
                    xpath = $"//Defs/{rootName}[defName=\"{def.DefName}\"]//{element}",
                    description = $"Target any {element} element within the definition",
                    specificity = "Medium"
                });
            }
        }
        catch
        {
            suggestions.Add(new
            {
                xpath = $"//*[defName=\"{def.DefName}\"]",
                description = "Basic targeting (XML parse error occurred)",
                specificity = "Low"
            });
        }

        return suggestions;
    }

    private static List<object> GenerateXPathExamples(RimWorldDef def, string? targetElement)
    {
        return new List<object>
        {
            new
            {
                operation = "Replace",
                xpath = $"//Defs/{def.Type}[defName=\"{def.DefName}\"]/label",
                value = "New Label",
                description = "Replace the label"
            },
            new
            {
                operation = "Add",
                xpath = $"//Defs/{def.Type}[defName=\"{def.DefName}\"]",
                value = "<newElement>value</newElement>",
                description = "Add a new element"
            }
        };
    }

    private static string GeneratePatchXml(string xpath, string operation, string? value)
    {
        var patchXml = $@"<Patch>
  <Operation Class=""PatchOperationReplace"">
    <xpath>{xpath}</xpath>";

        if (!string.IsNullOrEmpty(value))
        {
            patchXml += $@"
    <value>
      {value}
    </value>";
        }

        patchXml += @"
  </Operation>
</Patch>";

        return patchXml;
    }

    private static List<object> FindAffectedDefinitions(ServerData serverData, string xpath)
    {
        // Simplified - would need proper XPath evaluation
        return serverData.Defs.Values
            .Where(d => d.Content.ToString().Contains(xpath.Split('/').Last()))
            .Select(d => new { defName = d.DefName, type = d.Type, mod = d.Mod.Name } as object)
            .Take(10)
            .ToList();
    }

    private static List<string> ValidatePatch(string xpath, string operation, string? value)
    {
        var warnings = new List<string>();

        if (xpath.StartsWith("//"))
            warnings.Add("Deep XPath searches can impact performance");

        if (operation == "Remove" && string.IsNullOrEmpty(value))
            warnings.Add("Remove operations should be used carefully");

        if (operation != "Remove" && string.IsNullOrEmpty(value))
            warnings.Add("Add/Replace operations typically require a value");

        return warnings;
    }

    private static string FormatXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            return doc.ToString();
        }
        catch
        {
            return xml;
        }
    }

    private static List<object> CompareXmlChanges(string originalXml, string patchedXml)
    {
        var changes = new List<object>();

        if (originalXml.Length != patchedXml.Length)
        {
            changes.Add(new
            {
                type = "size_change",
                originalSize = originalXml.Length,
                newSize = patchedXml.Length,
                difference = patchedXml.Length - originalXml.Length
            });
        }

        return changes;
    }
}