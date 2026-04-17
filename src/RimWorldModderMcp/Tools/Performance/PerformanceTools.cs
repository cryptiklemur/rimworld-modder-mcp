using System.Text.Json;
using System.Xml.Linq;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.Performance;

public static class PerformanceTools
{
    [McpServerTool, Description("Locate duplicate/redundant definitions.")]
    public static string FindDuplicateContent(
        ServerData serverData,
        [Description("Optional: specific def type to check")] string? defType = null,
        [Description("Similarity threshold (0.0-1.0)")] double similarityThreshold = 0.9)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var duplicates = new List<object>();
        var processedPairs = new HashSet<string>();

        var defsToCheck = string.IsNullOrEmpty(defType)
            ? serverData.Defs.Values
            : serverData.Defs.Values.Where(d => d.Type.Equals(defType, StringComparison.OrdinalIgnoreCase));

        var defsList = defsToCheck.ToList();

        for (int i = 0; i < defsList.Count; i++)
        {
            for (int j = i + 1; j < defsList.Count; j++)
            {
                var def1 = defsList[i];
                var def2 = defsList[j];
                
                var pairKey = string.Compare(def1.DefName, def2.DefName) < 0 
                    ? $"{def1.DefName}:{def2.DefName}" 
                    : $"{def2.DefName}:{def1.DefName}";

                if (processedPairs.Contains(pairKey)) continue;
                processedPairs.Add(pairKey);

                var similarity = CalculateContentSimilarity(def1, def2);
                if (similarity >= similarityThreshold)
                {
                    duplicates.Add(new
                    {
                        def1 = new { defName = def1.DefName, mod = def1.Mod.Name, type = def1.Type },
                        def2 = new { defName = def2.DefName, mod = def2.Mod.Name, type = def2.Type },
                        similarity = Math.Round(similarity, 3),
                        duplicationType = GetDuplicationType(def1, def2),
                        commonElements = GetCommonElements(def1, def2),
                        recommendation = GetDuplicationRecommendation(def1, def2, similarity)
                    });
                }
            }
        }

        return JsonSerializer.Serialize(new
        {
            defType = defType ?? "all types",
            similarityThreshold = similarityThreshold,
            totalDuplicates = duplicates.Count,
            severeDuplicates = duplicates.Count(d => ((dynamic)d).similarity >= 0.95),
            duplicates = duplicates.OrderByDescending(d => ((dynamic)d).similarity).Take(50).ToList()
        });
    }

    [McpServerTool, Description("Recommend performance improvements.")]
    public static string SuggestOptimizations(
        ServerData serverData,
        [Description("Optional: specific mod package ID to analyze")] string? modPackageId = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var optimizations = new List<object>();

        var modsToCheck = string.IsNullOrEmpty(modPackageId)
            ? serverData.Mods.Values
            : serverData.Mods.Values.Where(m => m.PackageId == modPackageId);

        foreach (var mod in modsToCheck)
        {
            var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod.PackageId).ToList();
            var modOptimizations = new List<object>();

            // Check for excessive definition count
            if (modDefs.Count > 1000)
            {
                modOptimizations.Add(new
                {
                    type = "excessive_definitions",
                    severity = "Medium",
                    description = $"Mod has {modDefs.Count} definitions which may impact loading time",
                    recommendation = "Consider splitting into multiple smaller mods or removing unused definitions"
                });
            }

            // Check for large XML files
            var largeXmlDefs = modDefs.Where(d => d.Content.ToString().Length > 50000).ToList();
            if (largeXmlDefs.Any())
            {
                modOptimizations.Add(new
                {
                    type = "large_xml_definitions",
                    severity = "Medium",
                    description = $"{largeXmlDefs.Count} definitions have very large XML content",
                    recommendation = "Break down large definitions or optimize XML structure",
                    examples = largeXmlDefs.Take(3).Select(d => d.DefName).ToList()
                });
            }

            // Check for complex nested structures
            var complexDefs = modDefs.Where(d => HasComplexNesting(d)).ToList();
            if (complexDefs.Any())
            {
                modOptimizations.Add(new
                {
                    type = "complex_nesting",
                    severity = "Low",
                    description = $"{complexDefs.Count} definitions have deeply nested XML structures",
                    recommendation = "Simplify XML structure where possible to improve parsing performance",
                    examples = complexDefs.Take(3).Select(d => d.DefName).ToList()
                });
            }

            // Check for potential performance-heavy patches
            var modPatches = serverData.GlobalPatches.Where(p => p.Mod.PackageId == mod.PackageId).ToList();
            var expensivePatches = modPatches.Where(p => IsExpensivePatch(p)).ToList();
            if (expensivePatches.Any())
            {
                modOptimizations.Add(new
                {
                    type = "expensive_patches",
                    severity = "High",
                    description = $"{expensivePatches.Count} patches may have performance impact",
                    recommendation = "Optimize XPath expressions and avoid deep searches",
                    examples = expensivePatches.Take(3).Select(p => p.XPath).ToList()
                });
            }

            // Check for redundant definitions
            var redundantCount = CountRedundantDefinitions(modDefs);
            if (redundantCount > 0)
            {
                modOptimizations.Add(new
                {
                    type = "redundant_definitions",
                    severity = "Medium",
                    description = $"Approximately {redundantCount} definitions appear redundant",
                    recommendation = "Review and remove or consolidate duplicate definitions"
                });
            }

            if (modOptimizations.Any())
            {
                optimizations.Add(new
                {
                    mod = new { packageId = mod.PackageId, name = mod.Name },
                    totalOptimizations = modOptimizations.Count,
                    overallScore = CalculatePerformanceScore(modOptimizations),
                    optimizations = modOptimizations
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            scope = modPackageId ?? "all mods",
            totalModsAnalyzed = modsToCheck.Count(),
            modsWithOptimizations = optimizations.Count,
            overallRecommendations = GenerateOverallRecommendations(optimizations),
            modOptimizations = optimizations.OrderBy(o => ((dynamic)o).overallScore).Take(20).ToList()
        });
    }

    [McpServerTool, Description("Check texture sizes and formats.")]
    public static string AnalyzeTextureUsage(
        ServerData serverData,
        [Description("Optional: specific mod package ID to analyze")] string? modPackageId = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var textureAnalysis = new List<object>();

        var modsToCheck = string.IsNullOrEmpty(modPackageId)
            ? serverData.Mods.Values
            : serverData.Mods.Values.Where(m => m.PackageId == modPackageId);

        foreach (var mod in modsToCheck)
        {
            var textureInfo = AnalyzeModTextures(mod);
            if (((dynamic)textureInfo).totalTextures > 0)
            {
                textureAnalysis.Add(textureInfo);
            }
        }

        var totalTextures = textureAnalysis.Sum(t => (int)((dynamic)t).totalTextures);
        var totalSizeMB = textureAnalysis.Sum(t => (double)((dynamic)t).totalSizeMB);

        return JsonSerializer.Serialize(new
        {
            scope = modPackageId ?? "all mods",
            totalTextures = totalTextures,
            totalSizeMB = Math.Round((double)totalSizeMB, 2),
            averageSizePerTexture = totalTextures > 0 ? Math.Round((double)totalSizeMB / totalTextures, 3) : 0,
            largeTextures = textureAnalysis
                .SelectMany<object, object>(t => (IEnumerable<object>)((dynamic)t).largeTextures)
                .OrderByDescending(t => (double)((dynamic)t).sizeMB)
                .Take(20)
                .ToList(),
            recommendations = GenerateTextureRecommendations(textureAnalysis),
            modAnalysis = textureAnalysis.OrderByDescending(t => (double)((dynamic)t).totalSizeMB).Take(20).ToList()
        });
    }

    private static double CalculateContentSimilarity(RimWorldDef def1, RimWorldDef def2)
    {
        if (def1.Type != def2.Type) return 0.0;

        try
        {
            var xml1 = def1.Content.ToString();
            var xml2 = def2.Content.ToString();

            // Normalize XML for comparison
            var normalized1 = NormalizeXmlForComparison(xml1);
            var normalized2 = NormalizeXmlForComparison(xml2);

            // Calculate similarity using Levenshtein distance
            var maxLength = Math.Max(normalized1.Length, normalized2.Length);
            if (maxLength == 0) return 1.0;

            var distance = CalculateLevenshteinDistance(normalized1, normalized2);
            return 1.0 - (double)distance / maxLength;
        }
        catch
        {
            return 0.0;
        }
    }

    private static string NormalizeXmlForComparison(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            
            // Remove defName elements for comparison
            doc.Descendants("defName").Remove();
            
            // Sort elements for consistent comparison
            foreach (var element in doc.Descendants())
            {
                var sortedElements = element.Elements().OrderBy(e => e.Name.LocalName).ToList();
                element.Elements().Remove();
                element.Add(sortedElements);
            }
            
            return doc.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            return xml;
        }
    }

    private static int CalculateLevenshteinDistance(string s1, string s2)
    {
        var matrix = new int[s1.Length + 1, s2.Length + 1];

        for (int i = 0; i <= s1.Length; i++)
            matrix[i, 0] = i;
        for (int j = 0; j <= s2.Length; j++)
            matrix[0, j] = j;

        for (int i = 1; i <= s1.Length; i++)
        {
            for (int j = 1; j <= s2.Length; j++)
            {
                var cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                matrix[i, j] = Math.Min(Math.Min(
                    matrix[i - 1, j] + 1,
                    matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[s1.Length, s2.Length];
    }

    private static string GetDuplicationType(RimWorldDef def1, RimWorldDef def2)
    {
        if (def1.Mod.PackageId == def2.Mod.PackageId) return "same_mod";
        if (def1.DefName == def2.DefName) return "identical_names";
        return "cross_mod";
    }

    private static List<string> GetCommonElements(RimWorldDef def1, RimWorldDef def2)
    {
        var commonElements = new List<string>();
        
        try
        {
            var doc1 = XDocument.Parse(def1.Content.ToString());
            var doc2 = XDocument.Parse(def2.Content.ToString());
            
            var elements1 = doc1.Descendants().Select(e => e.Name.LocalName).ToHashSet();
            var elements2 = doc2.Descendants().Select(e => e.Name.LocalName).ToHashSet();
            
            commonElements = elements1.Intersect(elements2).Take(10).ToList();
        }
        catch
        {
            // Fallback to simple text analysis
        }
        
        return commonElements;
    }

    private static string GetDuplicationRecommendation(RimWorldDef def1, RimWorldDef def2, double similarity)
    {
        if (similarity >= 0.98)
            return "Consider removing one definition as they are nearly identical";
        if (similarity >= 0.9)
            return "Review for potential consolidation or inheritance";
        return "Monitor for unintended similarities";
    }

    private static bool HasComplexNesting(RimWorldDef def)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var maxDepth = GetMaxDepth(doc.Root);
            return maxDepth > 8;
        }
        catch
        {
            return false;
        }
    }

    private static int GetMaxDepth(XElement? element)
    {
        if (element == null) return 0;
        return element.Elements().Any() ? 1 + element.Elements().Max(GetMaxDepth) : 1;
    }

    private static bool IsExpensivePatch(PatchOperation patch)
    {
        var xpath = patch.XPath ?? "";
        
        // Check for performance-heavy patterns
        return xpath.StartsWith("//") || // Deep searches
               xpath.Contains("//*") || // Wildcard searches
               xpath.Count(c => c == '/') > 6 || // Very deep paths
               xpath.Contains("text()") || // Text searches
               xpath.Contains("preceding-sibling") || // Complex axis searches
               xpath.Contains("following-sibling");
    }

    private static int CountRedundantDefinitions(List<RimWorldDef> defs)
    {
        var redundantCount = 0;
        var seenContentHashes = new HashSet<string>();
        
        foreach (var def in defs)
        {
            var contentHash = def.Content.ToString().GetHashCode().ToString();
            if (seenContentHashes.Contains(contentHash))
            {
                redundantCount++;
            }
            else
            {
                seenContentHashes.Add(contentHash);
            }
        }
        
        return redundantCount;
    }

    private static string CalculatePerformanceScore(List<object> optimizations)
    {
        var highCount = optimizations.Count(o => ((dynamic)o).severity == "High");
        var mediumCount = optimizations.Count(o => ((dynamic)o).severity == "Medium");
        
        if (highCount > 0) return "Poor";
        if (mediumCount > 2) return "Needs Improvement";
        if (mediumCount > 0) return "Good";
        return "Excellent";
    }

    private static List<string> GenerateOverallRecommendations(List<object> optimizations)
    {
        var recommendations = new List<string>();
        
        var totalOptimizations = optimizations.Sum(o => (int)((dynamic)o).totalOptimizations);
        
        if (totalOptimizations > 50)
        {
            recommendations.Add("Consider reviewing mod load order and removing unnecessary mods");
        }
        
        recommendations.Add("Use RimWorld's built-in performance profiler to identify bottlenecks");
        recommendations.Add("Consider using texture compression and optimized formats");
        recommendations.Add("Remove unused assets and definitions to reduce memory usage");
        
        return recommendations;
    }

    private static object AnalyzeModTextures(ModInfo mod)
    {
        var textureFiles = new List<object>();
        var totalSize = 0L;
        
        try
        {
            var texturePaths = new[] { "Textures", "Materials" };
            var textureExtensions = new[] { ".png", ".jpg", ".jpeg", ".tga", ".psd" };
            
            foreach (var texturePath in texturePaths)
            {
                var fullPath = Path.Combine(mod.Path, texturePath);
                if (Directory.Exists(fullPath))
                {
                    var files = Directory.GetFiles(fullPath, "*.*", SearchOption.AllDirectories)
                        .Where(f => textureExtensions.Contains(Path.GetExtension(f).ToLower()));
                    
                    foreach (var file in files)
                    {
                        var fileInfo = new FileInfo(file);
                        var sizeMB = fileInfo.Length / (1024.0 * 1024.0);
                        totalSize += fileInfo.Length;
                        
                        if (sizeMB > 1.0) // Only track textures larger than 1MB
                        {
                            textureFiles.Add(new
                            {
                                fileName = Path.GetFileName(file),
                                relativePath = Path.GetRelativePath(mod.Path, file),
                                sizeMB = Math.Round(sizeMB, 2),
                                extension = Path.GetExtension(file),
                                recommendation = GetTextureRecommendation(sizeMB, Path.GetExtension(file))
                            });
                        }
                    }
                }
            }
        }
        catch
        {
            // Handle cases where directories don't exist or access is denied
        }
        
        return new
        {
            mod = new { packageId = mod.PackageId, name = mod.Name },
            totalTextures = textureFiles.Count,
            totalSizeMB = Math.Round(totalSize / (1024.0 * 1024.0), 2),
            largeTextures = textureFiles.OrderByDescending(t => ((dynamic)t).sizeMB).Take(10).ToList(),
            recommendations = GenerateModTextureRecommendations(textureFiles, totalSize)
        };
    }

    private static string GetTextureRecommendation(double sizeMB, string extension)
    {
        if (sizeMB > 10)
            return "Consider reducing resolution or using compression";
        if (sizeMB > 5)
            return "May benefit from optimization";
        if (extension.ToLower() == ".psd")
            return "Convert PSD files to PNG or JPG for better performance";
        return "Size acceptable";
    }

    private static List<string> GenerateModTextureRecommendations(List<object> textureFiles, long totalSize)
    {
        var recommendations = new List<string>();
        
        if (totalSize > 100 * 1024 * 1024) // > 100MB
        {
            recommendations.Add("Mod has high texture memory usage - consider optimization");
        }
        
        var psdCount = textureFiles.Count(t => ((dynamic)t).extension.ToLower() == ".psd");
        if (psdCount > 0)
        {
            recommendations.Add($"Convert {psdCount} PSD files to optimized formats");
        }
        
        if (textureFiles.Count > 20)
        {
            recommendations.Add("Consider texture atlasing for better performance");
        }
        
        return recommendations;
    }

    private static List<string> GenerateTextureRecommendations(List<object> textureAnalysis)
    {
        var recommendations = new List<string>();
        
        var totalSize = textureAnalysis.Sum(t => (double)((dynamic)t).totalSizeMB);
        
        if (totalSize > 500)
        {
            recommendations.Add("Total texture usage is high - consider texture optimization");
        }
        
        recommendations.Add("Use PNG for textures with transparency, JPG for others");
        recommendations.Add("Consider using texture compression to reduce memory usage");
        recommendations.Add("Remove unused texture files to improve loading times");
        
        return recommendations;
    }
}