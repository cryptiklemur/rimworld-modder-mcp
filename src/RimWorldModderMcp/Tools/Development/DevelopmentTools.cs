using System.Text.Json;
using System.Xml.Linq;
using System.Xml;
using System.Text;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.Development;

public static class DevelopmentTools
{
    private sealed record CompatibilityAnalysis(
        int TotalDefinitions,
        int PotentialConflicts,
        int PatchCount,
        List<string> Conflicts,
        List<string> Recommendations);

    [McpServerTool, Description("Use when you want missing or inconsistent translation coverage called out.")]
    public static string ValidateLocalization(
        ServerData serverData,
        [Description("Optional: specific mod package ID to check")] string? modPackageId = null,
        [Description("Language to validate (default: all)")] string? language = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var localizationReport = new List<object>();

        var modsToCheck = string.IsNullOrEmpty(modPackageId)
            ? serverData.Mods.Values
            : serverData.Mods.Values.Where(m => m.PackageId == modPackageId);

        foreach (var mod in modsToCheck)
        {
            var modReport = AnalyzeModLocalization(mod, language);
            if (modReport.hasTranslations)
            {
                localizationReport.Add(modReport);
            }
        }

        return JsonSerializer.Serialize(new
        {
            scope = modPackageId ?? "all mods",
            language = language ?? "all languages",
            totalModsWithTranslations = localizationReport.Count,
            overallCompleteness = CalculateOverallCompleteness(localizationReport),
            recommendations = GenerateLocalizationRecommendations(localizationReport),
            modReports = localizationReport.Take(20).ToList()
        });
    }

    [McpServerTool, Description("Use when you want unused textures, sounds, or other assets identified.")]
    public static string FindUnusedAssets(
        ServerData serverData,
        [Description("Optional: specific mod package ID to check")] string? modPackageId = null,
        [Description("Asset type to check (textures, sounds, all)")] string assetType = "all")
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var unusedAssets = new List<object>();

        var modsToCheck = string.IsNullOrEmpty(modPackageId)
            ? serverData.Mods.Values
            : serverData.Mods.Values.Where(m => m.PackageId == modPackageId);

        foreach (var mod in modsToCheck)
        {
            var modUnusedAssets = FindModUnusedAssets(serverData, mod, assetType);
            if (modUnusedAssets.Any())
            {
                unusedAssets.AddRange(modUnusedAssets);
            }
        }

        var totalSizeMB = unusedAssets.Sum(a => (double)((dynamic)a).sizeMB);

        return JsonSerializer.Serialize(new
        {
            scope = modPackageId ?? "all mods",
            assetType = assetType,
            totalUnusedAssets = unusedAssets.Count,
            totalUnusedSizeMB = Math.Round(totalSizeMB, 2),
            potentialSavingsMB = Math.Round(totalSizeMB, 2),
            recommendations = GenerateAssetRecommendations(unusedAssets),
            unusedAssets = unusedAssets.OrderByDescending(a => (double)((dynamic)a).sizeMB).Take(50).ToList()
        });
    }

    [McpServerTool, Description("Use when you want formatting and common XML issue checks across a mod.")]
    public static string LintXml(
        ServerData serverData,
        [Description("Optional: specific mod package ID to check")] string? modPackageId = null,
        [Description("Severity level to report (info, warning, error)")] string severityLevel = "warning")
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var lintResults = new List<object>();

        var defsToCheck = string.IsNullOrEmpty(modPackageId)
            ? serverData.Defs.Values
            : serverData.Defs.Values.Where(d => d.Mod.PackageId == modPackageId);

        foreach (var def in defsToCheck)
        {
            var issues = LintDefinitionXml(def, severityLevel);
            if (issues.Any())
            {
                lintResults.Add(new
                {
                    defName = def.DefName,
                    defType = def.Type,
                    mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                    filePath = def.FilePath,
                    issues = issues
                });
            }
        }

        var issueStats = CalculateIssueStatistics(lintResults);

        return JsonSerializer.Serialize(new
        {
            scope = modPackageId ?? "all mods",
            severityLevel = severityLevel,
            totalDefinitionsChecked = defsToCheck.Count(),
            definitionsWithIssues = lintResults.Count,
            issueStatistics = issueStats,
            recommendations = GenerateLintRecommendations(issueStats),
            results = lintResults.Take(50).ToList()
        });
    }

    [McpServerTool, Description("Use when you want a quick documentation summary for a mod's content.")]
    public static string GenerateDocumentation(
        ServerData serverData,
        [Description("Mod package ID to document")] string modPackageId,
        [Description("Documentation format (markdown, html, text)")] string format = "markdown")
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var mod = serverData.Mods.GetValueOrDefault(modPackageId);
        if (mod == null)
        {
            return JsonSerializer.Serialize(new { error = $"Mod '{modPackageId}' not found" });
        }

        var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == modPackageId).ToList();
        var documentation = GenerateModDocumentation(mod, modDefs, format);

        return JsonSerializer.Serialize(new
        {
            modPackageId = modPackageId,
            modName = mod.Name,
            format = format,
            totalDefinitions = modDefs.Count,
            documentationSize = documentation.Length,
            sections = GetDocumentationSections(modDefs),
            documentation = documentation
        });
    }

    [McpServerTool, Description("Use when you want a longer compatibility report for review or release notes.")]
    public static string CreateCompatibilityReport(
        ServerData serverData,
        [Description("Optional: specific mod package ID to analyze")] string? modPackageId = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var report = new StringBuilder();
        var summary = new Dictionary<string, object>();

        var modsToAnalyze = string.IsNullOrEmpty(modPackageId)
            ? serverData.Mods.Values.Take(20) // Limit to prevent massive reports
            : serverData.Mods.Values.Where(m => m.PackageId == modPackageId);

        foreach (var mod in modsToAnalyze)
        {
            var modAnalysis = AnalyzeModCompatibility(serverData, mod);
            report.AppendLine($"## Compatibility Analysis: {mod.Name}");
            report.AppendLine($"Package ID: {mod.PackageId}");
            report.AppendLine($"Load Order: {mod.LoadOrder}");
            report.AppendLine();
            
            report.AppendLine("### Definition Analysis");
            report.AppendLine($"- Total Definitions: {modAnalysis.TotalDefinitions}");
            report.AppendLine($"- Potential Conflicts: {modAnalysis.PotentialConflicts}");
            report.AppendLine($"- Patch Count: {modAnalysis.PatchCount}");
            report.AppendLine();

            if (modAnalysis.Conflicts.Any())
            {
                report.AppendLine("### Detected Conflicts");
                foreach (var conflict in modAnalysis.Conflicts.Take(5))
                {
                    report.AppendLine($"- {conflict}");
                }
                report.AppendLine();
            }

            if (modAnalysis.Recommendations.Any())
            {
                report.AppendLine("### Recommendations");
                foreach (var recommendation in modAnalysis.Recommendations)
                {
                    report.AppendLine($"- {recommendation}");
                }
                report.AppendLine();
            }

            report.AppendLine("---");
            report.AppendLine();
        }

        summary["totalModsAnalyzed"] = modsToAnalyze.Count();
        summary["reportSize"] = report.Length;
        summary["generatedAt"] = DateTime.UtcNow;

        return JsonSerializer.Serialize(new
        {
            scope = modPackageId ?? "multiple mods",
            summary = summary,
            fullReport = report.ToString()
        });
    }

    [McpServerTool, Description("Use when you need filtered definitions exported for external inspection or tooling.")]
    public static string ExportDefinitions(
        ServerData serverData,
        [Description("Export format (json, xml, csv, yaml)")] string format = "json",
        [Description("Optional: definition type filter")] string? defType = null,
        [Description("Optional: mod package ID filter")] string? modPackageId = null,
        [Description("Maximum number of definitions to export")] int maxDefinitions = 1000)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var defsToExport = serverData.Defs.Values.AsEnumerable();

        if (!string.IsNullOrEmpty(defType))
        {
            defsToExport = defsToExport.Where(d => d.Type.Equals(defType, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrEmpty(modPackageId))
        {
            defsToExport = defsToExport.Where(d => d.Mod.PackageId == modPackageId);
        }

        var definitionsList = defsToExport.Take(maxDefinitions).ToList();
        var exportedData = ExportDefinitionsInFormat(definitionsList, format);

        return JsonSerializer.Serialize(new
        {
            format = format,
            filters = new
            {
                defType = defType,
                modPackageId = modPackageId
            },
            exportedCount = definitionsList.Count,
            totalAvailable = defsToExport.Count(),
            exportSize = exportedData.Length,
            exportedData = exportedData
        });
    }

    private static dynamic AnalyzeModLocalization(ModInfo mod, string? language)
    {
        var hasTranslations = false;
        var languages = new List<string>();
        var completeness = new Dictionary<string, double>();
        var missingKeys = new List<string>();

        try
        {
            var languagesPath = Path.Combine(mod.Path, "Languages");
            if (Directory.Exists(languagesPath))
            {
                hasTranslations = true;
                var languageDirs = Directory.GetDirectories(languagesPath);
                
                foreach (var langDir in languageDirs)
                {
                    var langName = Path.GetFileName(langDir);
                    languages.Add(langName);
                    
                    if (language == null || langName.Equals(language, StringComparison.OrdinalIgnoreCase))
                    {
                        var keysFilePattern = Path.Combine(langDir, "Keyed", "*.xml");
                        var keyFiles = Directory.GetFiles(Path.GetDirectoryName(keysFilePattern)!, "*.xml", SearchOption.AllDirectories);
                        
                        var translatedKeys = 0;
                        var totalKeys = 0;
                        
                        foreach (var keyFile in keyFiles)
                        {
                            var keys = ExtractKeysFromFile(keyFile);
                            totalKeys += keys.Count;
                            translatedKeys += keys.Count(k => !string.IsNullOrWhiteSpace(k.Value));
                        }
                        
                        if (totalKeys > 0)
                        {
                            completeness[langName] = (double)translatedKeys / totalKeys;
                        }
                    }
                }
            }
        }
        catch
        {
            // Handle directory access issues
        }

        return new
        {
            mod = new { packageId = mod.PackageId, name = mod.Name },
            hasTranslations = hasTranslations,
            languages = languages,
            completeness = completeness,
            averageCompleteness = completeness.Values.Any() ? completeness.Values.Average() : 0.0,
            missingKeys = missingKeys.Take(10).ToList()
        };
    }

    private static Dictionary<string, string> ExtractKeysFromFile(string filePath)
    {
        var keys = new Dictionary<string, string>();
        
        try
        {
            var doc = XDocument.Load(filePath);
            foreach (var element in doc.Descendants())
            {
                if (!element.HasElements && !string.IsNullOrEmpty(element.Name.LocalName))
                {
                    keys[element.Name.LocalName] = element.Value;
                }
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return keys;
    }

    private static double CalculateOverallCompleteness(List<object> reports)
    {
        if (!reports.Any()) return 0.0;
        
        return reports.Average(r => (double)((dynamic)r).averageCompleteness);
    }

    private static List<string> GenerateLocalizationRecommendations(List<object> reports)
    {
        var recommendations = new List<string>();
        
        var lowCompleteness = reports.Count(r => (double)((dynamic)r).averageCompleteness < 0.5);
        if (lowCompleteness > 0)
        {
            recommendations.Add($"{lowCompleteness} mods have low translation completeness");
        }
        
        recommendations.Add("Ensure all user-facing text has translation keys");
        recommendations.Add("Test translations with different languages enabled");
        recommendations.Add("Consider community translation contributions");
        
        return recommendations;
    }

    private static List<object> FindModUnusedAssets(ServerData serverData, ModInfo mod, string assetType)
    {
        var unusedAssets = new List<object>();
        
        try
        {
            var assetPaths = new List<string>();
            
            if (assetType == "all" || assetType == "textures")
            {
                var texturesPath = Path.Combine(mod.Path, "Textures");
                if (Directory.Exists(texturesPath))
                {
                    assetPaths.AddRange(Directory.GetFiles(texturesPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => IsTextureFile(f)));
                }
            }
            
            if (assetType == "all" || assetType == "sounds")
            {
                var soundsPath = Path.Combine(mod.Path, "Sounds");
                if (Directory.Exists(soundsPath))
                {
                    assetPaths.AddRange(Directory.GetFiles(soundsPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => IsSoundFile(f)));
                }
            }
            
            var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod.PackageId);
            var referencedAssets = ExtractAssetReferences(modDefs);
            
            foreach (var assetPath in assetPaths)
            {
                var assetName = GetAssetReferenceName(assetPath);
                if (!referencedAssets.Contains(assetName, StringComparer.OrdinalIgnoreCase))
                {
                    var fileInfo = new FileInfo(assetPath);
                    unusedAssets.Add(new
                    {
                        fileName = Path.GetFileName(assetPath),
                        relativePath = Path.GetRelativePath(mod.Path, assetPath),
                        fullPath = assetPath,
                        sizeMB = Math.Round(fileInfo.Length / (1024.0 * 1024.0), 3),
                        type = GetAssetType(assetPath),
                        mod = new { packageId = mod.PackageId, name = mod.Name }
                    });
                }
            }
        }
        catch
        {
            // Handle directory access issues
        }
        
        return unusedAssets;
    }

    private static bool IsTextureFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLower();
        return new[] { ".png", ".jpg", ".jpeg", ".tga", ".psd" }.Contains(ext);
    }

    private static bool IsSoundFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLower();
        return new[] { ".wav", ".ogg", ".mp3" }.Contains(ext);
    }

    private static HashSet<string> ExtractAssetReferences(IEnumerable<RimWorldDef> defs)
    {
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        
        foreach (var def in defs)
        {
            try
            {
                var content = def.Content.ToString();
                var doc = XDocument.Parse(content);
                
                // Look for common asset reference patterns
                var textureElements = doc.Descendants().Where(e => 
                    e.Name.LocalName.Contains("texture", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Contains("icon", StringComparison.OrdinalIgnoreCase) ||
                    e.Name.LocalName.Contains("graphic", StringComparison.OrdinalIgnoreCase));
                
                foreach (var element in textureElements)
                {
                    if (!string.IsNullOrEmpty(element.Value))
                    {
                        references.Add(element.Value);
                    }
                }
                
                // Look for sound references
                var soundElements = doc.Descendants().Where(e =>
                    e.Name.LocalName.Contains("sound", StringComparison.OrdinalIgnoreCase));
                
                foreach (var element in soundElements)
                {
                    if (!string.IsNullOrEmpty(element.Value))
                    {
                        references.Add(element.Value);
                    }
                }
            }
            catch
            {
                // Handle XML parsing errors
            }
        }
        
        return references;
    }

    private static string GetAssetReferenceName(string filePath)
    {
        // Convert file path to reference name (without extension, relative to Textures/Sounds)
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        var directory = Path.GetDirectoryName(filePath);
        
        if (directory?.Contains("Textures") == true)
        {
            var texturesIndex = directory.IndexOf("Textures");
            if (texturesIndex >= 0)
            {
                var relativePath = directory.Substring(texturesIndex + "Textures".Length).TrimStart('\\', '/');
                return string.IsNullOrEmpty(relativePath) ? fileName : $"{relativePath}/{fileName}".Replace('\\', '/');
            }
        }
        
        return fileName;
    }

    private static string GetAssetType(string filePath)
    {
        if (IsTextureFile(filePath)) return "texture";
        if (IsSoundFile(filePath)) return "sound";
        return "unknown";
    }

    private static List<string> GenerateAssetRecommendations(List<object> unusedAssets)
    {
        var recommendations = new List<string>();
        
        if (unusedAssets.Count > 0)
        {
            recommendations.Add($"Remove {unusedAssets.Count} unused assets to reduce mod size");
        }
        
        var largeAssets = unusedAssets.Count(a => (double)((dynamic)a).sizeMB > 1.0);
        if (largeAssets > 0)
        {
            recommendations.Add($"Priority: Remove {largeAssets} large unused assets first");
        }
        
        recommendations.Add("Verify assets are truly unused before removal");
        recommendations.Add("Consider keeping assets that may be used by patches or mods");
        
        return recommendations;
    }

    private static List<object> LintDefinitionXml(RimWorldDef def, string severityLevel)
    {
        var issues = new List<object>();
        
        try
        {
            var content = def.Content.ToString();
            var doc = XDocument.Parse(content);
            
            // Check for common XML issues
            CheckDuplicateElements(doc, issues, def);
            CheckMissingRequiredElements(doc, issues, def);
            CheckInvalidValues(doc, issues, def);
            CheckPerformanceIssues(doc, issues, def);
            CheckNamingConventions(doc, issues, def);
        }
        catch (XmlException ex)
        {
            issues.Add(new
            {
                severity = "error",
                type = "xml_parse_error",
                message = $"XML parsing failed: {ex.Message}",
                line = ex.LineNumber,
                position = ex.LinePosition
            });
        }
        catch (Exception ex)
        {
            issues.Add(new
            {
                severity = "error",
                type = "analysis_error",
                message = $"Analysis failed: {ex.Message}"
            });
        }
        
        // Filter by severity level
        return FilterIssuesBySeverity(issues, severityLevel);
    }

    private static void CheckDuplicateElements(XDocument doc, List<object> issues, RimWorldDef def)
    {
        var elementGroups = doc.Descendants()
            .Where(e => !e.HasElements)
            .GroupBy(e => e.Name.LocalName)
            .Where(g => g.Count() > 1);
        
        foreach (var group in elementGroups)
        {
            if (ShouldBeUnique(group.Key))
            {
                issues.Add(new
                {
                    severity = "warning",
                    type = "duplicate_element",
                    message = $"Duplicate element '{group.Key}' found {group.Count()} times",
                    element = group.Key
                });
            }
        }
    }

    private static void CheckMissingRequiredElements(XDocument doc, List<object> issues, RimWorldDef def)
    {
        var requiredElements = GetRequiredElementsForType(def.Type);
        
        foreach (var required in requiredElements)
        {
            if (!doc.Descendants(required).Any())
            {
                issues.Add(new
                {
                    severity = "error",
                    type = "missing_required_element",
                    message = $"Required element '{required}' is missing",
                    element = required
                });
            }
        }
    }

    private static void CheckInvalidValues(XDocument doc, List<object> issues, RimWorldDef def)
    {
        // Check for numeric values in text fields, empty required fields, etc.
        foreach (var element in doc.Descendants())
        {
            if (!element.HasElements && !string.IsNullOrEmpty(element.Value))
            {
                if (ShouldBeNumeric(element.Name.LocalName) && !IsNumeric(element.Value))
                {
                    issues.Add(new
                    {
                        severity = "error",
                        type = "invalid_numeric_value",
                        message = $"Element '{element.Name.LocalName}' should be numeric but contains '{element.Value}'",
                        element = element.Name.LocalName,
                        value = element.Value
                    });
                }
            }
        }
    }

    private static void CheckPerformanceIssues(XDocument doc, List<object> issues, RimWorldDef def)
    {
        var depth = GetMaxDepth(doc.Root);
        if (depth > 10)
        {
            issues.Add(new
            {
                severity = "info",
                type = "deep_nesting",
                message = $"XML has deep nesting (depth: {depth}) which may impact performance",
                depth = depth
            });
        }
        
        var elementCount = doc.Descendants().Count();
        if (elementCount > 1000)
        {
            issues.Add(new
            {
                severity = "warning",
                type = "large_definition",
                message = $"Definition has many elements ({elementCount}) which may impact loading time",
                elementCount = elementCount
            });
        }
    }

    private static void CheckNamingConventions(XDocument doc, List<object> issues, RimWorldDef def)
    {
        if (def.DefName.Contains(" "))
        {
            issues.Add(new
            {
                severity = "warning",
                type = "naming_convention",
                message = "DefName should not contain spaces",
                defName = def.DefName
            });
        }
        
        if (!char.IsUpper(def.DefName[0]))
        {
            issues.Add(new
            {
                severity = "info",
                type = "naming_convention",
                message = "DefName should start with uppercase letter",
                defName = def.DefName
            });
        }
    }

    private static List<object> FilterIssuesBySeverity(List<object> issues, string severityLevel)
    {
        var severityOrder = new Dictionary<string, int>
        {
            { "info", 1 },
            { "warning", 2 },
            { "error", 3 }
        };
        
        var minSeverity = severityOrder.GetValueOrDefault(severityLevel, 2);
        
        return issues.Where(i => {
            var severity = ((dynamic)i).severity as string ?? "info";
            return severityOrder.GetValueOrDefault(severity, 1) >= minSeverity;
        }).ToList();
    }

    private static bool ShouldBeUnique(string elementName)
    {
        var uniqueElements = new[] { "defName", "label", "description" };
        return uniqueElements.Contains(elementName, StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> GetRequiredElementsForType(string defType)
    {
        // Basic required elements for most definition types
        return new List<string> { "defName" };
    }

    private static bool ShouldBeNumeric(string elementName)
    {
        var numericElements = new[] { "mass", "volume", "marketValue", "workToMake", "hitPoints" };
        return numericElements.Contains(elementName, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsNumeric(string value)
    {
        return double.TryParse(value, out _);
    }

    private static int GetMaxDepth(XElement? element)
    {
        if (element == null) return 0;
        return element.Elements().Any() ? 1 + element.Elements().Max(GetMaxDepth) : 1;
    }

    private static object CalculateIssueStatistics(List<object> lintResults)
    {
        var allIssues = lintResults.SelectMany(r => (IEnumerable<object>)((dynamic)r).issues);
        
        return new
        {
            totalIssues = allIssues.Count(),
            errorCount = allIssues.Count(i => ((dynamic)i).severity == "error"),
            warningCount = allIssues.Count(i => ((dynamic)i).severity == "warning"),
            infoCount = allIssues.Count(i => ((dynamic)i).severity == "info"),
            issueTypes = allIssues.GroupBy(i => ((dynamic)i).type).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private static List<string> GenerateLintRecommendations(object issueStats)
    {
        var recommendations = new List<string>();
        var stats = (dynamic)issueStats;
        
        if (stats.errorCount > 0)
        {
            recommendations.Add($"Fix {stats.errorCount} critical errors first");
        }
        
        if (stats.warningCount > 10)
        {
            recommendations.Add("High number of warnings - consider reviewing XML structure");
        }
        
        recommendations.Add("Use XML validation tools during development");
        recommendations.Add("Follow RimWorld XML conventions and naming standards");
        
        return recommendations;
    }

    private static string GenerateModDocumentation(ModInfo mod, List<RimWorldDef> defs, string format)
    {
        var doc = new StringBuilder();
        
        if (format == "markdown")
        {
            doc.AppendLine($"# {mod.Name}");
            doc.AppendLine();
            doc.AppendLine($"**Package ID:** {mod.PackageId}");
            doc.AppendLine($"**Load Order:** {mod.LoadOrder}");
            doc.AppendLine($"**Path:** {mod.Path}");
            doc.AppendLine();
            
            var defsByType = defs.GroupBy(d => d.Type).OrderBy(g => g.Key);
            
            doc.AppendLine("## Definitions");
            doc.AppendLine();
            
            foreach (var group in defsByType)
            {
                doc.AppendLine($"### {group.Key} ({group.Count()})");
                doc.AppendLine();
                
                foreach (var def in group.OrderBy(d => d.DefName).Take(20))
                {
                    doc.AppendLine($"- **{def.DefName}**");
                    if (def.Parent != null)
                    {
                        doc.AppendLine($"  - Parent: {def.Parent}");
                    }
                    if (def.Abstract)
                    {
                        doc.AppendLine($"  - Abstract: Yes");
                    }
                }
                
                if (group.Count() > 20)
                {
                    doc.AppendLine($"- ... and {group.Count() - 20} more");
                }
                
                doc.AppendLine();
            }
        }
        else if (format == "html")
        {
            doc.AppendLine($"<h1>{mod.Name}</h1>");
            doc.AppendLine($"<p><strong>Package ID:</strong> {mod.PackageId}</p>");
            doc.AppendLine($"<p><strong>Total Definitions:</strong> {defs.Count}</p>");
            // Add more HTML formatting as needed
        }
        else // text format
        {
            doc.AppendLine($"MOD: {mod.Name}");
            doc.AppendLine($"Package ID: {mod.PackageId}");
            doc.AppendLine($"Total Definitions: {defs.Count}");
            doc.AppendLine();
            
            foreach (var def in defs.Take(50))
            {
                doc.AppendLine($"{def.Type}: {def.DefName}");
            }
        }
        
        return doc.ToString();
    }

    private static List<string> GetDocumentationSections(List<RimWorldDef> defs)
    {
        var sections = new List<string> { "Overview", "Definitions by Type" };
        
        var defTypes = defs.Select(d => d.Type).Distinct().OrderBy(t => t);
        sections.AddRange(defTypes);
        
        return sections;
    }

    private static CompatibilityAnalysis AnalyzeModCompatibility(ServerData serverData, ModInfo mod)
    {
        var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod.PackageId).ToList();
        var conflicts = new List<string>();
        var recommendations = new List<string>();
        
        // Analyze for potential conflicts
        var duplicateNames = modDefs.GroupBy(d => d.DefName)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key);
        
        foreach (var dupName in duplicateNames)
        {
            conflicts.Add($"Duplicate definition name: {dupName}");
        }
        
        // Check for load order issues
        if (mod.LoadOrder < 10 && !mod.IsCore)
        {
            recommendations.Add("Consider loading this mod after core and expansion mods");
        }
        
        var patchCount = serverData.GlobalPatches.Count(p => p.Mod.PackageId == mod.PackageId);
        if (patchCount > 50)
        {
            recommendations.Add("High patch count may indicate compatibility issues");
        }
        
        return new CompatibilityAnalysis(
            modDefs.Count,
            conflicts.Count,
            patchCount,
            conflicts,
            recommendations);
    }

    private static string ExportDefinitionsInFormat(List<RimWorldDef> defs, string format)
    {
        return format.ToLower() switch
        {
            "json" => JsonSerializer.Serialize(defs.Select(d => new
            {
                defName = d.DefName,
                type = d.Type,
                parent = d.Parent,
                @abstract = d.Abstract,
                mod = d.Mod.PackageId,
                content = d.Content.ToString()
            }), new JsonSerializerOptions { WriteIndented = true }),
            
            "csv" => ExportToCsv(defs),
            "xml" => ExportToXml(defs),
            "yaml" => ExportToYaml(defs),
            _ => JsonSerializer.Serialize(new { error = $"Unsupported format: {format}" })
        };
    }

    private static string ExportToCsv(List<RimWorldDef> defs)
    {
        var csv = new StringBuilder();
        csv.AppendLine("DefName,Type,Parent,Abstract,Mod,ContentLength");
        
        foreach (var def in defs)
        {
            csv.AppendLine($"\"{def.DefName}\",\"{def.Type}\",\"{def.Parent}\",{def.Abstract},\"{def.Mod.PackageId}\",{def.Content.ToString().Length}");
        }
        
        return csv.ToString();
    }

    private static string ExportToXml(List<RimWorldDef> defs)
    {
        var xml = new StringBuilder();
        xml.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        xml.AppendLine("<Definitions>");
        
        foreach (var def in defs)
        {
            xml.AppendLine($"  <Definition>");
            xml.AppendLine($"    <DefName>{def.DefName}</DefName>");
            xml.AppendLine($"    <Type>{def.Type}</Type>");
            xml.AppendLine($"    <Parent>{def.Parent}</Parent>");
            xml.AppendLine($"    <Abstract>{def.Abstract}</Abstract>");
            xml.AppendLine($"    <Mod>{def.Mod.PackageId}</Mod>");
            xml.AppendLine($"  </Definition>");
        }
        
        xml.AppendLine("</Definitions>");
        return xml.ToString();
    }

    private static string ExportToYaml(List<RimWorldDef> defs)
    {
        var yaml = new StringBuilder();
        yaml.AppendLine("definitions:");
        
        foreach (var def in defs)
        {
            yaml.AppendLine($"  - defName: \"{def.DefName}\"");
            yaml.AppendLine($"    type: \"{def.Type}\"");
            yaml.AppendLine($"    parent: \"{def.Parent}\"");
            yaml.AppendLine($"    abstract: {def.Abstract.ToString().ToLower()}");
            yaml.AppendLine($"    mod: \"{def.Mod.PackageId}\"");
        }
        
        return yaml.ToString();
    }
}
