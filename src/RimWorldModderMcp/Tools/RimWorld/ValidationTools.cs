using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class ValidationTools
{
    [McpServerTool, Description("Use when one loaded definition looks wrong and you want XML and structure checks for that def.")]
    public static string ValidateDef(
        ServerData serverData,
        [Description("The name of the definition to validate")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        if (!serverData.Defs.TryGetValue(defName, out var def))
        {
            return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });
        }

        var validationResults = new List<object>();

        // Basic validation rules
        if (string.IsNullOrEmpty(def.DefName))
            validationResults.Add(new { severity = "error", message = "DefName is empty" });

        if (string.IsNullOrEmpty(def.Type))
            validationResults.Add(new { severity = "error", message = "Type is not specified" });

        // Check if parent exists when specified
        if (!string.IsNullOrEmpty(def.Parent))
        {
            var parentExists = serverData.Defs.ContainsKey(def.Parent) || 
                             serverData.Defs.Values.Any(d => d.DefName == def.Parent && d.Abstract);
            
            if (!parentExists)
            {
                validationResults.Add(new { severity = "warning", message = $"Parent '{def.Parent}' not found" });
            }
        }

        // Check for valid XML structure
        try
        {
            XDocument.Parse(def.Content.ToString());
        }
        catch (Exception ex)
        {
            validationResults.Add(new { severity = "error", message = $"Invalid XML: {ex.Message}" });
        }

        return JsonSerializer.Serialize(new
        {
            defName = def.DefName,
            type = def.Type,
            isValid = validationResults.All(r => ((dynamic)r).severity != "error"),
            validationResults = validationResults
        });
    }

    [McpServerTool, Description("Use when you need the outgoing references and dependencies from one definition.")]
    public static string GetDefDependencies(
        ServerData serverData,
        [Description("The name of the definition to analyze")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        if (!serverData.Defs.TryGetValue(defName, out var def))
        {
            return JsonSerializer.Serialize(new { error = $"Definition '{defName}' not found" });
        }

        var dependencies = new List<object>();
        var content = def.Content.ToString();

        // Find references to other defs in the content
        foreach (var otherDef in serverData.Defs.Values)
        {
            if (otherDef.DefName == def.DefName) continue;

            if (content.Contains(otherDef.DefName, StringComparison.OrdinalIgnoreCase))
            {
                dependencies.Add(new
                {
                    defName = otherDef.DefName,
                    type = otherDef.Type,
                    mod = new
                    {
                        packageId = otherDef.Mod.PackageId,
                        name = otherDef.Mod.Name
                    },
                    context = ExtractDependencyContext(content, otherDef.DefName)
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            defName = def.DefName,
            totalDependencies = dependencies.Count,
            dependencies = dependencies.Take(50).ToList()
        });
    }

    [McpServerTool, Description("Use when you want to test whether an XPath is valid and what it will match before writing a patch.")]
    public static string ValidateXPath(
        ServerData serverData,
        [Description("The XPath expression to validate and test")] string xpath,
        [Description("Optional: test against a specific definition")] string? defName = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        // Get test targets
        var testTargets = !string.IsNullOrEmpty(defName) && serverData.Defs.ContainsKey(defName)
            ? new[] { serverData.Defs[defName] }
            : serverData.Defs.Values.Take(10).ToArray();

        if (testTargets.Length == 0)
        {
            return JsonSerializer.Serialize(new { error = "No definitions available for testing" });
        }

        var validationResults = ValidateXPathSyntax(xpath);
        
        if (!validationResults.isValidXPath)
        {
            var syntaxErrors = new List<string> { "Invalid XPath syntax" };
            
            return JsonSerializer.Serialize(new
            {
                xpath = xpath,
                testedAgainst = defName ?? $"{testTargets.Length} sample definitions",
                isValid = false,
                syntaxErrors = syntaxErrors,
                matchResults = new List<object>(),
                suggestions = GetXPathSuggestions(xpath),
                summary = new
                {
                    totalTested = testTargets.Length,
                    matches = 0,
                    syntaxValid = false
                }
            });
        }

        // Test XPath against definitions
        var matchCount = 0;
        var matchResults = new List<object>();

        foreach (var def in testTargets)
        {
            var testResult = TestXPathAgainstDefinition(xpath, def);
            matchResults.Add(testResult);
            
            if ((bool)((dynamic)testResult).hasMatch)
            {
                matchCount++;
            }
        }

        var suggestions = GenerateXPathSuggestions(xpath, matchResults);

        return JsonSerializer.Serialize(new
        {
            xpath = xpath,
            testedAgainst = defName ?? $"{testTargets.Length} sample definitions",
            isValid = true,
            syntaxErrors = new List<string>(),
            matchResults = matchResults.Take(10).ToList(), // Limit output
            suggestions = suggestions,
            summary = new
            {
                totalTested = testTargets.Length,
                matches = matchCount,
                syntaxValid = true
            }
        });
    }

    private static string ExtractDependencyContext(string content, string dependencyName)
    {
        var index = content.IndexOf(dependencyName, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var start = Math.Max(0, index - 30);
            var length = Math.Min(content.Length - start, 80);
            var context = content.Substring(start, length).Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (start > 0) context = "..." + context;
            if (start + length < content.Length) context = context + "...";
            return context;
        }
        return string.Empty;
    }

    private static (bool isValidXPath, string error) ValidateXPathSyntax(string xpath)
    {
        try
        {
            // Basic XPath syntax validation
            if (string.IsNullOrWhiteSpace(xpath))
                return (false, "XPath cannot be empty");

            // Simple validation - check for balanced brackets and quotes
            var openBrackets = xpath.Count(c => c == '[');
            var closeBrackets = xpath.Count(c => c == ']');
            if (openBrackets != closeBrackets)
                return (false, "Unmatched brackets in XPath");

            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static object TestXPathAgainstDefinition(string xpath, RimWorldDef def)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var matches = doc.XPathSelectElements(xpath);
            var matchList = matches?.ToList() ?? new List<XElement>();

            return new
            {
                defName = def.DefName,
                hasMatch = matchList.Count > 0,
                matchCount = matchList.Count,
                matches = matchList.Take(3).Select(m => new
                {
                    element = m.Name.LocalName,
                    value = m.Value,
                    attributes = m.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value)
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            return new
            {
                defName = def.DefName,
                hasMatch = false,
                matchCount = 0,
                error = ex.Message
            };
        }
    }

    private static List<string> GetXPathSuggestions(string xpath)
    {
        var suggestions = new List<string>();
        
        if (string.IsNullOrWhiteSpace(xpath))
        {
            suggestions.Add("Try starting with common paths like '//defName' or '//label'");
            return suggestions;
        }

        // Basic suggestions based on common patterns
        if (!xpath.StartsWith("//") && !xpath.StartsWith("/"))
        {
            suggestions.Add($"Consider using '//{xpath}' to search anywhere in the document");
        }

        if (!xpath.Contains("@") && !xpath.Contains("["))
        {
            suggestions.Add($"Try '{xpath}[@*]' to find elements with any attribute");
            suggestions.Add($"Try '{xpath}[text()]' to find elements with text content");
        }

        return suggestions;
    }

    private static List<string> GenerateXPathSuggestions(string xpath, List<object> matchResults)
    {
        var suggestions = new List<string>();
        
        var matchCount = matchResults.Count(r => (bool)((dynamic)r).hasMatch);
        
        if (matchCount == 0)
        {
            suggestions.Add("No matches found. Try a broader XPath like '//li' or '//*[text()]'");
        }
        else if (matchCount == matchResults.Count)
        {
            suggestions.Add("XPath matches all tested definitions. Consider adding filters to narrow results");
        }

        return suggestions;
    }
}
