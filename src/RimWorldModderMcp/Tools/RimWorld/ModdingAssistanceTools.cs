using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class ModdingAssistanceTools
{
    [McpServerTool, Description("Use when you are naming new content and want candidate DefNames that follow RimWorld conventions.")]
    public static string SuggestDefName(
        ServerData serverData,
        [Description("The type of definition (ThingDef, PawnKindDef, etc.)")] string defType,
        [Description("Base name or description of what you want to create")] string baseName,
        [Description("Optional: your mod's packageId for prefixing")] string? modPackageId = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        if (string.IsNullOrWhiteSpace(baseName))
        {
            return JsonSerializer.Serialize(new { error = "Base name cannot be empty" });
        }

        // Clean and format the base name
        var cleanedBase = CleanDefNameBase(baseName);
        
        // Get existing defs of this type for pattern analysis
        var existingDefs = serverData.Defs.Values
            .Where(d => d.Type.Equals(defType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var suggestions = new List<object>();
        
        // Pattern 1: Simple cleaned name
        var simple = cleanedBase;
        suggestions.Add(CreateSuggestion(simple, "Simple clean name", !IsDefNameTaken(simple, serverData)));

        // Pattern 2: With mod prefix (if provided)
        if (!string.IsNullOrEmpty(modPackageId))
        {
            var modPrefix = ExtractModPrefix(modPackageId);
            var withPrefix = $"{modPrefix}_{cleanedBase}";
            suggestions.Add(CreateSuggestion(withPrefix, "With mod prefix", !IsDefNameTaken(withPrefix, serverData)));
        }

        // Pattern 3: Type-prefixed (common pattern)
        var typePrefix = defType.Replace("Def", "").Replace("def", "");
        var withTypePrefix = $"{typePrefix}_{cleanedBase}";
        suggestions.Add(CreateSuggestion(withTypePrefix, "With type prefix", !IsDefNameTaken(withTypePrefix, serverData)));

        // Pattern 4: Based on similar existing defs
        if (existingDefs.Count > 0)
        {
            var commonPatterns = AnalyzeNamingPatterns(existingDefs);
            foreach (var pattern in commonPatterns.Take(3))
            {
                var patternSuggestion = ApplyNamingPattern(cleanedBase, pattern);
                if (!suggestions.Any(s => ((dynamic)s).defName == patternSuggestion))
                {
                    suggestions.Add(CreateSuggestion(patternSuggestion, $"Following common pattern: {pattern.description}", !IsDefNameTaken(patternSuggestion, serverData)));
                }
            }
        }

        // Pattern 5: Numbered variations if conflicts exist
        var numberedSuggestions = new List<object>();
        foreach (var suggestion in suggestions.Take(3))
        {
            var baseSuggestion = ((dynamic)suggestion).defName;
            if (IsDefNameTaken(baseSuggestion, serverData))
            {
                for (int i = 2; i <= 5; i++)
                {
                    var numbered = $"{baseSuggestion}{i}";
                    if (!IsDefNameTaken(numbered, serverData))
                    {
                        numberedSuggestions.Add(CreateSuggestion(numbered, $"Numbered variant of {baseSuggestion}", true));
                        break;
                    }
                }
            }
        }
        suggestions.AddRange(numberedSuggestions);

        return JsonSerializer.Serialize(new
        {
            defType = defType,
            baseName = baseName,
            cleanedBase = cleanedBase,
            totalExistingOfType = existingDefs.Count,
            suggestions = suggestions.Take(8).ToList(),
            conventions = new
            {
                general = "Use PascalCase, avoid spaces and special characters",
                prefixing = "Consider prefixing with mod name for uniqueness",
                descriptive = "Be descriptive but concise",
                avoid = new[] { "spaces", "special characters", "numbers at start", "reserved words" }
            }
        });
    }

    [McpServerTool, Description("Use when you want to validate a proposed DefName before committing to it.")]
    public static string CheckNamingConventions(
        ServerData serverData,
        [Description("The DefName to check")] string defName,
        [Description("Optional: the definition type for context")] string? defType = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        if (string.IsNullOrWhiteSpace(defName))
        {
            return JsonSerializer.Serialize(new { error = "DefName cannot be empty" });
        }

        var violations = new List<object>();
        var suggestions = new List<string>();
        var score = 100;

        // Check basic naming rules
        if (char.IsLower(defName[0]))
        {
            violations.Add(new { rule = "PascalCase", severity = "warning", message = "DefName should start with uppercase letter" });
            suggestions.Add($"Consider: {char.ToUpper(defName[0]) + defName[1..]}");
            score -= 10;
        }

        if (defName.Contains(" "))
        {
            violations.Add(new { rule = "NoSpaces", severity = "error", message = "DefName cannot contain spaces" });
            suggestions.Add($"Consider: {defName.Replace(" ", "")}");
            score -= 20;
        }

        if (Regex.IsMatch(defName, @"[^a-zA-Z0-9_]"))
        {
            violations.Add(new { rule = "AlphaNumericOnly", severity = "error", message = "DefName should only contain letters, numbers, and underscores" });
            var cleaned = Regex.Replace(defName, @"[^a-zA-Z0-9_]", "");
            suggestions.Add($"Consider: {cleaned}");
            score -= 15;
        }

        if (char.IsDigit(defName[0]))
        {
            violations.Add(new { rule = "NoLeadingNumbers", severity = "warning", message = "DefName should not start with a number" });
            score -= 5;
        }

        // Check against reserved words
        var reservedWords = new[] { "base", "new", "class", "abstract", "parent", "def", "null" };
        if (reservedWords.Contains(defName.ToLower()))
        {
            violations.Add(new { rule = "NoReservedWords", severity = "error", message = "DefName should not use reserved words" });
            score -= 25;
        }

        // Check length
        if (defName.Length > 50)
        {
            violations.Add(new { rule = "ReasonableLength", severity = "warning", message = "DefName is quite long, consider shortening" });
            score -= 5;
        }

        if (defName.Length < 3)
        {
            violations.Add(new { rule = "MinimumLength", severity = "warning", message = "DefName is very short, consider being more descriptive" });
            score -= 5;
        }

        // Check if already exists
        var exists = IsDefNameTaken(defName, serverData);
        if (exists)
        {
            violations.Add(new { rule = "Uniqueness", severity = "error", message = "DefName already exists in loaded definitions" });
            score -= 50;
        }

        // Check against patterns in same type
        if (!string.IsNullOrEmpty(defType))
        {
            var sameTypeDefs = serverData.Defs.Values
                .Where(d => d.Type.Equals(defType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            
            if (sameTypeDefs.Count > 0)
            {
                var patterns = AnalyzeNamingPatterns(sameTypeDefs);
                var followsCommonPattern = patterns.Any(p => defName.Contains(p.pattern) || p.pattern.Contains("_") && defName.Contains("_"));
                
                if (!followsCommonPattern && patterns.Count > 0)
                {
                    violations.Add(new { rule = "ConsistencyWithType", severity = "info", message = $"DefName doesn't follow common patterns for {defType}" });
                    score -= 3;
                }
            }
        }

        var overallGrade = score >= 90 ? "Excellent" : score >= 75 ? "Good" : score >= 60 ? "Fair" : "Poor";

        return JsonSerializer.Serialize(new
        {
            defName = defName,
            defType = defType,
            isValid = !violations.Any(v => ((dynamic)v).severity == "error"),
            score = Math.Max(0, score),
            grade = overallGrade,
            exists = exists,
            violations = violations,
            suggestions = suggestions,
            conventionsChecked = new[]
            {
                "PascalCase naming",
                "No spaces or special characters", 
                "No reserved words",
                "Reasonable length",
                "Uniqueness",
                "Type consistency"
            }
        });
    }

    [McpServerTool, Description("Use when you need a translation-key inventory from a mod's loaded content.")]
    public static string FindTranslationKeys(
        ServerData serverData,
        [Description("Optional: filter to specific mod packageId")] string? modPackageId = null,
        [Description("Optional: filter to specific definition type")] string? defType = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var defsToCheck = serverData.Defs.Values.AsEnumerable();
        
        if (!string.IsNullOrEmpty(modPackageId))
        {
            defsToCheck = defsToCheck.Where(d => d.Mod.PackageId.Equals(modPackageId, StringComparison.OrdinalIgnoreCase));
        }
        
        if (!string.IsNullOrEmpty(defType))
        {
            defsToCheck = defsToCheck.Where(d => d.Type.Equals(defType, StringComparison.OrdinalIgnoreCase));
        }

        var translationKeys = new List<object>();
        var keyGroups = new Dictionary<string, List<object>>();

        foreach (var def in defsToCheck)
        {
            try
            {
                var doc = XDocument.Parse(def.Content.ToString());
                var translatableElements = FindTranslatableElements(doc);
                
                foreach (var element in translatableElements)
                {
                    var key = GenerateTranslationKey(def, element);
                    var group = GetTranslationGroup(element.elementName);
                    
                    var translationInfo = new
                    {
                        key = key,
                        defName = def.DefName,
                        defType = def.Type,
                        modPackageId = def.Mod.PackageId,
                        modName = def.Mod.Name,
                        elementPath = element.path,
                        elementName = element.elementName,
                        currentValue = element.value,
                        group = group,
                        isRequired = IsTranslationRequired(element.elementName),
                        suggestedTranslation = element.value // Starting point for translation
                    };
                    
                    translationKeys.Add(translationInfo);
                    
                    if (!keyGroups.ContainsKey(group))
                        keyGroups[group] = new List<object>();
                    keyGroups[group].Add(translationInfo);
                }
            }
            catch (Exception)
            {
                // Skip malformed XML
                continue;
            }
        }

        var statistics = new
        {
            totalKeys = translationKeys.Count,
            byType = keyGroups.ToDictionary(g => g.Key, g => g.Value.Count),
            byMod = translationKeys.GroupBy(k => ((dynamic)k).modPackageId)
                                  .ToDictionary(g => g.Key, g => g.Count()),
            requiredKeys = translationKeys.Count(k => (bool)((dynamic)k).isRequired),
            optionalKeys = translationKeys.Count(k => !(bool)((dynamic)k).isRequired)
        };

        return JsonSerializer.Serialize(new
        {
            filter = new { modPackageId, defType },
            statistics = statistics,
            translationKeys = translationKeys.Take(100).ToList(), // Limit output
            keyGroups = keyGroups.ToDictionary(g => g.Key, g => g.Value.Take(20).ToList()),
            guidelines = new
            {
                keyFormat = "ModPackageId.DefName.ElementPath",
                fileStructure = "Languages/English/Keyed/ for UI strings, Languages/English/DefInjected/ for definition strings",
                bestPractices = new[]
                {
                    "Group related keys together",
                    "Use descriptive key names",
                    "Provide context comments for translators",
                    "Test translations with long/short text variants"
                }
            }
        });
    }

    [McpServerTool, Description("Use when you want scaffolded About.xml-style metadata for a mod release or new project.")]
    public static string GenerateModMetadata(
        ServerData serverData,
        [Description("The mod name")] string modName,
        [Description("The author name")] string author,
        [Description("Description of the mod")] string description,
        [Description("Target RimWorld version (e.g., '1.4', '1.5')")] string rimworldVersion,
        [Description("Optional: mod packageId (auto-generated if not provided)")] string? packageId = null,
        [Description("Optional: comma-separated list of dependency packageIds")] string? dependencies = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        if (string.IsNullOrWhiteSpace(modName) || string.IsNullOrWhiteSpace(author))
        {
            return JsonSerializer.Serialize(new { error = "Mod name and author are required" });
        }

        // Generate packageId if not provided
        var finalPackageId = packageId ?? GeneratePackageId(author, modName);
        
        // Parse dependencies
        var dependencyList = new List<string>();
        if (!string.IsNullOrEmpty(dependencies))
        {
            dependencyList = dependencies.Split(',')
                                       .Select(d => d.Trim())
                                       .Where(d => !string.IsNullOrEmpty(d))
                                       .ToList();
        }

        // Generate About.xml content
        var aboutXml = GenerateAboutXml(modName, author, description, rimworldVersion, finalPackageId, dependencyList);
        
        // Generate metadata files
        var files = new List<object>
        {
            new
            {
                filename = "About/About.xml",
                content = aboutXml,
                description = "Main mod metadata file"
            }
        };

        // Add preview image placeholder
        files.Add(new
        {
            filename = "About/Preview.png",
            content = "[BINARY FILE - 512x512 PNG image recommended]",
            description = "Mod preview image (512x512 recommended)"
        });

        // Add PublishedFileId if needed
        files.Add(new
        {
            filename = "About/PublishedFileId.txt",
            content = "[STEAM_WORKSHOP_ID]",
            description = "Steam Workshop ID (created after first upload)"
        });

        // Add mod metadata
        if (!string.IsNullOrEmpty(rimworldVersion))
        {
            files.Add(new
            {
                filename = "About/Manifest.xml",
                content = GenerateManifestXml(finalPackageId, rimworldVersion, dependencyList),
                description = "Dependency manifest for mod managers"
            });
        }

        // Validate metadata
        var validation = ValidateModMetadata(finalPackageId, modName, author, rimworldVersion, dependencyList, serverData);

        return JsonSerializer.Serialize(new
        {
            modName = modName,
            author = author,
            packageId = finalPackageId,
            rimworldVersion = rimworldVersion,
            dependencies = dependencyList,
            files = files,
            validation = validation,
            guidelines = new
            {
                packageIdFormat = "Author.ModName (no spaces, PascalCase)",
                descriptionTips = "Be clear about what the mod does, mention compatibility, include credits",
                versionSupport = "Support latest stable version, consider beta compatibility",
                dependencies = "Only include hard dependencies, soft dependencies should be optional"
            }
        });
    }

    [McpServerTool, Description("Use when you want compatibility hints for a target RimWorld version.")]
    public static string CheckVersionCompatibility(
        ServerData serverData,
        [Description("Optional: specific mod packageId to check")] string? modPackageId = null,
        [Description("Target RimWorld version to check compatibility against")] string targetVersion = "1.5")
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var modsToCheck = string.IsNullOrEmpty(modPackageId)
            ? serverData.Mods.Values.Where(m => !m.IsCore).ToList()
            : serverData.Mods.Values.Where(m => m.PackageId.Equals(modPackageId, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!modsToCheck.Any())
        {
            return JsonSerializer.Serialize(new { error = "No mods found to check" });
        }

        var compatibilityResults = new List<object>();

        foreach (var mod in modsToCheck)
        {
            var compatibility = AnalyzeVersionCompatibility(mod, targetVersion, serverData);
            compatibilityResults.Add(compatibility);
        }

        var summary = new
        {
            targetVersion = targetVersion,
            totalModsChecked = compatibilityResults.Count,
            compatible = compatibilityResults.Count(r => ((dynamic)r).isCompatible),
            incompatible = compatibilityResults.Count(r => !((dynamic)r).isCompatible),
            unknown = compatibilityResults.Count(r => ((dynamic)r).status == "unknown")
        };

        return JsonSerializer.Serialize(new
        {
            targetVersion = targetVersion,
            summary = summary,
            results = compatibilityResults.Take(50).ToList(), // Limit output
            versioningGuidelines = new
            {
                supportedVersions = "Specify exact versions in About.xml",
                backwardCompatibility = "Test with older versions when possible",
                updateProcedure = "Update targetVersion in About.xml, test thoroughly, update dependencies",
                commonIssues = new[]
                {
                    "Assembly references to game code",
                    "Usage of deprecated XML elements",
                    "Hardcoded version-specific values",
                    "Mod dependencies on different versions"
                }
            }
        });
    }

    [McpServerTool, Description("Use when you want a recommended load order for a set of mods based on dependencies and conflicts.")]
    public static string SuggestLoadOrder(
        ServerData serverData,
        [Description("Optional: comma-separated list of specific mod packageIds to analyze")] string? modPackageIds = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var modsToAnalyze = new List<ModInfo>();
        
        if (!string.IsNullOrEmpty(modPackageIds))
        {
            var specifiedIds = modPackageIds.Split(',').Select(id => id.Trim()).ToList();
            modsToAnalyze = serverData.Mods.Values
                .Where(m => specifiedIds.Contains(m.PackageId, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        else
        {
            modsToAnalyze = serverData.Mods.Values.Where(m => !m.IsCore).ToList();
        }

        if (!modsToAnalyze.Any())
        {
            return JsonSerializer.Serialize(new { error = "No mods found to analyze" });
        }

        // Analyze dependencies and conflicts
        var dependencyGraph = BuildDependencyGraph(modsToAnalyze, serverData);
        var conflicts = DetectModConflicts(modsToAnalyze, serverData);
        var suggestedOrder = CalculateOptimalLoadOrder(modsToAnalyze, dependencyGraph, conflicts);

        var currentOrder = modsToAnalyze.OrderBy(m => m.LoadOrder).ToList();
        var orderChanges = new List<object>();

        for (int i = 0; i < suggestedOrder.Count; i++)
        {
            var currentIndex = currentOrder.FindIndex(m => m.PackageId == suggestedOrder[i].PackageId);
            if (currentIndex != i)
            {
                orderChanges.Add(new
                {
                    modPackageId = suggestedOrder[i].PackageId,
                    modName = suggestedOrder[i].Name,
                    currentPosition = currentIndex + 1,
                    suggestedPosition = i + 1,
                    reason = GetLoadOrderReason(suggestedOrder[i], dependencyGraph, conflicts)
                });
            }
        }

        return JsonSerializer.Serialize(new
        {
            totalMods = modsToAnalyze.Count,
            suggestedOrder = suggestedOrder.Select((m, i) => new
            {
                position = i + 1,
                packageId = m.PackageId,
                name = m.Name,
                currentPosition = m.LoadOrder + 1,
                moveNeeded = m.LoadOrder != i
            }).ToList(),
            changes = orderChanges,
            dependencyIssues = dependencyGraph.Where(d => d.Value.issues.Any()).ToDictionary(
                d => d.Key,
                d => d.Value.issues
            ),
            conflicts = conflicts.Take(10).ToList(),
            loadOrderPrinciples = new
            {
                coreFirst = "Core game and framework mods load first",
                dependenciesBeforeDependents = "Dependencies must load before mods that depend on them",
                conflictSeparation = "Conflicting mods should be separated when possible",
                librariesFirst = "Library mods should load before content mods that use them",
                patchesLast = "Patches and overrides should generally load last"
            }
        });
    }

    // Helper methods
    private static string CleanDefNameBase(string baseName)
    {
        // Remove spaces and special characters, convert to PascalCase
        var cleaned = Regex.Replace(baseName, @"[^a-zA-Z0-9\s]", "");
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Select(w => char.ToUpper(w[0]) + w[1..].ToLower()));
    }

    private static object CreateSuggestion(string defName, string reason, bool isAvailable)
    {
        return new
        {
            defName = defName,
            reason = reason,
            isAvailable = isAvailable,
            confidence = isAvailable ? "high" : "low"
        };
    }

    private static bool IsDefNameTaken(string defName, ServerData serverData)
    {
        return serverData.Defs.ContainsKey(defName) || 
               serverData.Defs.Values.Any(d => d.DefName.Equals(defName, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractModPrefix(string packageId)
    {
        var parts = packageId.Split('.');
        return parts.Length > 1 ? parts[0] : packageId.Substring(0, Math.Min(4, packageId.Length));
    }

    private static List<(string pattern, string description)> AnalyzeNamingPatterns(List<RimWorldDef> defs)
    {
        var patterns = new List<(string pattern, string description)>();
        
        var namesWithUnderscores = defs.Where(d => d.DefName.Contains("_")).ToList();
        if (namesWithUnderscores.Count > defs.Count * 0.3)
        {
            patterns.Add(("_", "underscore separation"));
        }
        
        var prefixGroups = defs.GroupBy(d => d.DefName.Split('_', '_')[0]).Where(g => g.Count() > 2);
        foreach (var group in prefixGroups.Take(3))
        {
            patterns.Add((group.Key, $"common prefix '{group.Key}'"));
        }
        
        return patterns;
    }

    private static string ApplyNamingPattern(string baseName, (string pattern, string description) pattern)
    {
        if (pattern.pattern == "_")
            return baseName; // Already handled by CleanDefNameBase
        
        return $"{pattern.pattern}_{baseName}";
    }

    private static List<(string elementName, string path, string value)> FindTranslatableElements(XDocument doc)
    {
        var translatableElements = new List<(string, string, string)>();
        var commonTranslatableElements = new[] { "label", "description", "labelShort", "descriptionShort", "jobString", "gerund" };
        
        foreach (var element in doc.Descendants())
        {
            if (commonTranslatableElements.Contains(element.Name.LocalName, StringComparer.OrdinalIgnoreCase))
            {
                var path = GetXPath(element);
                translatableElements.Add((element.Name.LocalName, path, element.Value));
            }
        }
        
        return translatableElements;
    }

    private static string GetXPath(XElement element)
    {
        var parts = new List<string>();
        var current = element;
        
        while (current != null && current.Parent != null)
        {
            parts.Insert(0, current.Name.LocalName);
            current = current.Parent;
        }
        
        return string.Join(".", parts);
    }

    private static string GenerateTranslationKey(RimWorldDef def, (string elementName, string path, string value) element)
    {
        return $"{def.Mod.PackageId}.{def.DefName}.{element.elementName}";
    }

    private static string GetTranslationGroup(string elementName)
    {
        return elementName.ToLower() switch
        {
            "label" or "labelshort" => "labels",
            "description" or "descriptionshort" => "descriptions", 
            "jobstring" or "gerund" => "jobs",
            _ => "misc"
        };
    }

    private static bool IsTranslationRequired(string elementName)
    {
        var requiredElements = new[] { "label", "description" };
        return requiredElements.Contains(elementName.ToLower());
    }

    private static string GeneratePackageId(string author, string modName)
    {
        var cleanAuthor = Regex.Replace(author, @"[^a-zA-Z0-9]", "");
        var cleanModName = Regex.Replace(modName, @"[^a-zA-Z0-9]", "");
        return $"{cleanAuthor}.{cleanModName}";
    }

    private static string GenerateAboutXml(string modName, string author, string description, string version, string packageId, List<string> dependencies)
    {
        var xml = new XElement("ModMetaData",
            new XElement("name", modName),
            new XElement("author", author),
            new XElement("packageId", packageId),
            new XElement("description", description),
            new XElement("supportedVersions",
                new XElement("li", version)
            )
        );

        if (dependencies.Any())
        {
            var dependenciesElement = new XElement("modDependencies");
            foreach (var dep in dependencies)
            {
                dependenciesElement.Add(new XElement("li",
                    new XElement("packageId", dep),
                    new XElement("displayName", "[Dependency Name]")
                ));
            }
            xml.Add(dependenciesElement);
        }

        return xml.ToString();
    }

    private static string GenerateManifestXml(string packageId, string version, List<string> dependencies)
    {
        var manifest = new XElement("Manifest",
            new XElement("identifier", packageId),
            new XElement("version", "1.0.0"),
            new XElement("targetVersions",
                new XElement("li", version)
            )
        );

        if (dependencies.Any())
        {
            var dependenciesElement = new XElement("dependencies");
            foreach (var dep in dependencies)
            {
                dependenciesElement.Add(new XElement("li", dep));
            }
            manifest.Add(dependenciesElement);
        }

        return manifest.ToString();
    }

    private static object ValidateModMetadata(string packageId, string modName, string author, string version, List<string> dependencies, ServerData serverData)
    {
        var issues = new List<string>();
        
        if (serverData.Mods.Values.Any(m => m.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add($"PackageId '{packageId}' already exists in loaded mods");
        }
        
        if (packageId.Length > 60)
        {
            issues.Add("PackageId is quite long, consider shortening");
        }
        
        foreach (var dep in dependencies)
        {
            if (!serverData.Mods.Values.Any(m => m.PackageId.Equals(dep, StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add($"Dependency '{dep}' not found in loaded mods");
            }
        }
        
        return new
        {
            isValid = !issues.Any(),
            issues = issues,
            suggestions = issues.Any() ? new[] { "Review issues above", "Test metadata with mod manager" } : new[] { "Metadata looks good!" }
        };
    }

    private static object AnalyzeVersionCompatibility(ModInfo mod, string targetVersion, ServerData serverData)
    {
        var supportedVersions = mod.SupportedVersions ?? new List<string>();
        var isCompatible = supportedVersions.Any(v => v.StartsWith(targetVersion));
        
        var issues = new List<string>();
        
        if (!isCompatible && supportedVersions.Any())
        {
            issues.Add($"Mod supports versions {string.Join(", ", supportedVersions)} but not {targetVersion}");
        }
        
        // Check for version-specific issues by analyzing definitions
        var versionIssues = DetectVersionSpecificIssues(mod, targetVersion, serverData);
        issues.AddRange(versionIssues);
        
        return new
        {
            modPackageId = mod.PackageId,
            modName = mod.Name,
            currentSupportedVersions = supportedVersions,
            targetVersion = targetVersion,
            isCompatible = isCompatible,
            status = isCompatible ? "compatible" : supportedVersions.Any() ? "incompatible" : "unknown",
            issues = issues,
            confidence = issues.Any() ? "low" : "high"
        };
    }

    private static List<string> DetectVersionSpecificIssues(ModInfo mod, string targetVersion, ServerData serverData)
    {
        var issues = new List<string>();
        
        // This would require deep analysis of mod content
        // For now, return basic checks
        
        var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod.PackageId);
        var hasComplexPatches = modDefs.Any(d => d.Content.ToString().Contains("Class=") || d.Content.ToString().Contains("Assembly="));
        
        if (hasComplexPatches)
        {
            issues.Add("Mod contains assembly references that may break with version changes");
        }
        
        return issues;
    }

    private static Dictionary<string, (List<string> dependencies, List<string> issues)> BuildDependencyGraph(List<ModInfo> mods, ServerData serverData)
    {
        var graph = new Dictionary<string, (List<string> dependencies, List<string> issues)>();
        
        foreach (var mod in mods)
        {
            var dependencies = new List<string>();
            var issues = new List<string>();
            
            // Analyze mod content for references to other mods
            var modDefs = serverData.Defs.Values.Where(d => d.Mod.PackageId == mod.PackageId);
            foreach (var def in modDefs)
            {
                var content = def.Content.ToString();
                foreach (var otherMod in mods)
                {
                    if (otherMod.PackageId != mod.PackageId && content.Contains(otherMod.PackageId))
                    {
                        if (!dependencies.Contains(otherMod.PackageId))
                            dependencies.Add(otherMod.PackageId);
                    }
                }
            }
            
            graph[mod.PackageId] = (dependencies, issues);
        }
        
        return graph;
    }

    private static List<object> DetectModConflicts(List<ModInfo> mods, ServerData serverData)
    {
        var conflicts = new List<object>();
        
        // Look for definitions with same DefName from different mods
        var defGroups = serverData.Defs.Values
            .Where(d => mods.Any(m => m.PackageId == d.Mod.PackageId))
            .GroupBy(d => d.DefName)
            .Where(g => g.Count() > 1);
        
        foreach (var group in defGroups)
        {
            var conflictingMods = group.Select(d => d.Mod).Distinct().ToList();
            if (conflictingMods.Count > 1)
            {
                conflicts.Add(new
                {
                    type = "duplicate_defname",
                    defName = group.Key,
                    conflictingMods = conflictingMods.Select(m => new { packageId = m.PackageId, name = m.Name }).ToList(),
                    severity = "high"
                });
            }
        }
        
        return conflicts;
    }

    private static List<ModInfo> CalculateOptimalLoadOrder(List<ModInfo> mods, Dictionary<string, (List<string> dependencies, List<string> issues)> dependencyGraph, List<object> conflicts)
    {
        // Simple topological sort based on dependencies
        var sorted = new List<ModInfo>();
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        
        foreach (var mod in mods)
        {
            if (!visited.Contains(mod.PackageId))
            {
                TopologicalSort(mod.PackageId, mods, dependencyGraph, visiting, visited, sorted);
            }
        }
        
        return sorted;
    }

    private static void TopologicalSort(string modId, List<ModInfo> mods, Dictionary<string, (List<string> dependencies, List<string> issues)> graph, HashSet<string> visiting, HashSet<string> visited, List<ModInfo> result)
    {
        if (visiting.Contains(modId)) return; // Cycle detected, skip
        if (visited.Contains(modId)) return;
        
        visiting.Add(modId);
        
        if (graph.TryGetValue(modId, out var info))
        {
            foreach (var dependency in info.dependencies)
            {
                TopologicalSort(dependency, mods, graph, visiting, visited, result);
            }
        }
        
        visiting.Remove(modId);
        visited.Add(modId);
        
        var mod = mods.FirstOrDefault(m => m.PackageId == modId);
        if (mod != null && !result.Contains(mod))
        {
            result.Add(mod);
        }
    }

    private static string GetLoadOrderReason(ModInfo mod, Dictionary<string, (List<string> dependencies, List<string> issues)> dependencyGraph, List<object> conflicts)
    {
        if (dependencyGraph.TryGetValue(mod.PackageId, out var info))
        {
            if (info.dependencies.Any())
                return $"Depends on: {string.Join(", ", info.dependencies.Take(3))}";
            if (info.issues.Any())
                return $"Issues: {string.Join(", ", info.issues.Take(2))}";
        }
        
        return "Optimal position based on analysis";
    }
}
