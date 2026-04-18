using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using RimWorldModderMcp.Attributes;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class ModWorkflowTools
{
    private static readonly Regex QuotedValueRegex = new("\"([^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex FilePathRegex = new(@"([A-Za-z]:\\[^\s:""]+\.xml|/[^\s:""]+\.xml)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NumberRegex = new(@"\b\d+\b", RegexOptions.Compiled);
    private static readonly Regex DefNameXPathRegex = new(@"defName\s*=\s*[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AngleBracketRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Dictionary<string, string[]> OfficialContentAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"] = ["Core", "ludeon.rimworld"],
        ["Royalty"] = ["Royalty", "ludeon.rimworld.royalty"],
        ["Ideology"] = ["Ideology", "ludeon.rimworld.ideology"],
        ["Biotech"] = ["Biotech", "ludeon.rimworld.biotech"],
        ["Anomaly"] = ["Anomaly", "ludeon.rimworld.anomaly"]
    };

    private static readonly HashSet<string> IgnoredLeafFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "defName",
        "label",
        "description",
        "jobString",
        "reportString",
        "baseDescription",
        "texPath",
        "iconPath",
        "texturePath",
        "uiIconPath",
        "graphicData",
        "soundImpactExpected",
        "soundImpactUnexpected",
        "packagedDefName"
    };

    private static readonly HashSet<string> RuntimeClassFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "Class",
        "thingClass",
        "workerClass",
        "compClass",
        "drawerClass",
        "modExtensions",
        "entityClass"
    };

    private sealed record SymbolEntry(string Value, string Path, string FieldName);
    private sealed record ResolvedReference(string FromDef, string ToDef, string Path, string ToType, ModInfo TargetMod);
    private sealed record DlcDependencyFinding(string Severity, string Kind, string ForbiddenContent, string Message, string? ModPackageId = null, string? DefName = null, string? FilePath = null, string? XPath = null, string? Reference = null);
    private sealed record AuditFinding(string Severity, string Code, string Message, string? ModPackageId = null, string? DefName = null, string? FilePath = null, string? XPath = null, string? Suggestion = null);
    private sealed record PatchHotspot(string Severity, string XPath, int PatchCount, IReadOnlyList<string> Operations, IReadOnlyList<string> Mods, IReadOnlyList<string> Files, IReadOnlyList<string> TargetDefs);
    private sealed record LogIncident(string Category, string Signature, string Headline, string Text, IReadOnlyList<string> ContextLines, IReadOnlyList<string> MentionedDefs, IReadOnlyList<string> MentionedFiles, IReadOnlyList<string> MentionedMods);
    private sealed record LogGroup(string Category, string Signature, string Headline, int Count, IReadOnlyList<string> MentionedDefs, IReadOnlyList<string> MentionedFiles, IReadOnlyList<string> MentionedMods);

    [McpServerTool, Description("Use when Player.log has startup or runtime errors and you want grouped causes quickly.")]
    public static string TriagePlayerLog(
        ServerData serverData,
        ProjectContext projectContext,
        [Description("Absolute path to the RimWorld Player.log or Player-prev.log file")] string? logPath = null,
        [Description("Optional: only keep incidents clearly tied to this mod package ID")] string? modPackageId = null,
        [Description("Maximum grouped issue buckets to return")] int maxGroups = 15)
    {
        var resolvedLogPath = logPath ?? projectContext.LogPath;
        if (string.IsNullOrWhiteSpace(resolvedLogPath))
        {
            return Serialize(new { error = "No log path was provided. Pass logPath or set logPath in .rimworld-modder-mcp.json." });
        }

        if (!File.Exists(resolvedLogPath))
        {
            return Serialize(new { error = $"Log file '{resolvedLogPath}' was not found" });
        }

        ModInfo? targetMod = null;
        if (!string.IsNullOrWhiteSpace(modPackageId))
        {
            targetMod = ResolveMod(serverData, modPackageId);
            if (targetMod == null)
            {
                return Serialize(new { error = $"Mod '{modPackageId}' was not found" });
            }
        }

        var incidents = AnalyzePlayerLog(serverData, resolvedLogPath, targetMod);
        var groupedIncidents = incidents
            .GroupBy(incident => incident.Signature, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var sample = group.First();
                return new
                {
                    category = sample.Category,
                    signature = sample.Signature,
                    count = group.Count(),
                    headline = sample.Headline,
                    mentionedDefs = group.SelectMany(item => item.MentionedDefs).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
                    mentionedFiles = group.SelectMany(item => item.MentionedFiles).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList(),
                    mentionedMods = group.SelectMany(item => item.MentionedMods).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
                    examples = group.Take(2).Select(item => new
                    {
                        item.Headline,
                        context = item.ContextLines.Take(4).ToList()
                    }).ToList()
                };
            })
            .OrderByDescending(group => group.count)
            .ThenBy(group => group.category, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxGroups))
            .ToList();

        return Serialize(new
        {
            logPath = resolvedLogPath,
            filteredMod = targetMod == null ? null : new { packageId = targetMod.PackageId, name = targetMod.Name },
            summary = new
            {
                totalIncidents = incidents.Count,
                groupedIssues = groupedIncidents.Count,
                categories = groupedIncidents
                    .GroupBy(group => group.category, StringComparer.OrdinalIgnoreCase)
                    .Select(group => new { category = group.Key, issueGroups = group.Count(), incidents = group.Sum(item => item.count) })
                    .OrderByDescending(group => group.incidents)
                    .ToList()
            },
            groups = groupedIncidents
        });
    }

    [McpServerTool, Description("Use when a loaded def may disagree with parent, reference, or class-like runtime expectations.")]
    public static string ValidateDefAgainstRuntime(
        ServerData serverData,
        [Description("Definition name to validate")] string defName)
    {
        var def = FindDefByName(serverData, defName);
        if (def == null)
        {
            return Serialize(new { error = $"Definition '{defName}' was not found" });
        }

        var checks = new List<object>();
        var errorCount = 0;
        var warningCount = 0;

        try
        {
            _ = XDocument.Parse(def.Content.ToString());
            checks.Add(new { severity = "info", code = "xml_parse", message = "Definition XML parsed successfully" });
        }
        catch (Exception ex)
        {
            errorCount++;
            checks.Add(new { severity = "error", code = "xml_parse", message = ex.Message });
        }

        if (!string.IsNullOrWhiteSpace(def.Parent))
        {
            var parent = FindDefByName(serverData, def.Parent);
            if (parent == null)
            {
                warningCount++;
                checks.Add(new { severity = "warning", code = "missing_parent", message = $"Parent '{def.Parent}' was not found in loaded defs or abstract defs" });
            }
            else
            {
                checks.Add(new { severity = "info", code = "parent", message = $"Parent '{parent.DefName}' resolved from {parent.Mod.Name}" });
            }
        }

        var unresolvedReferences = GetUnresolvedReferences(def, serverData)
            .Take(15)
            .Select(reference => new
            {
                reference.Value,
                reference.Path
            })
            .ToList();

        if (unresolvedReferences.Count > 0)
        {
            warningCount += unresolvedReferences.Count;
        }

        var runtimeClassSignals = EnumerateSymbolEntries(def.Content)
            .Where(entry => IsRuntimeClassPath(entry.Path))
            .DistinctBy(entry => $"{entry.Path}:{entry.Value}", StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(entry => new
            {
                entry.Value,
                entry.Path,
                validation = "unverified"
            })
            .ToList();

        var overrides = serverData.Conflicts
            .Where(conflict => conflict.Type == ConflictType.Override &&
                               string.Equals(conflict.DefName, def.DefName, StringComparison.OrdinalIgnoreCase))
            .Select(conflict => new
            {
                severity = ToSeverity(conflict.Severity),
                conflict.Description,
                mods = conflict.Mods.Select(mod => new { packageId = mod.PackageId, name = mod.Name }).ToList()
            })
            .ToList();

        if (overrides.Count > 0)
        {
            warningCount += overrides.Count;
        }

        var status = errorCount > 0 ? "fail" : warningCount > 0 ? "warn" : "pass";

        return Serialize(new
        {
            validationLevel = "loaded-data-only",
            status,
            def = new
            {
                defName = def.DefName,
                type = def.Type,
                mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                filePath = def.FilePath,
                parent = def.Parent
            },
            summary = new
            {
                errors = errorCount,
                warnings = warningCount,
                runtimeClassSignals = runtimeClassSignals.Count,
                unresolvedReferences = unresolvedReferences.Count,
                overrides = overrides.Count
            },
            checks,
            unresolvedReferences,
            runtimeClassSignals,
            overrides,
            limitations = new[]
            {
                "This tool does not decompile assemblies or verify C# members in-process.",
                "Runtime class values are surfaced as signals only and remain unverified unless another service validates them."
            }
        });
    }

    [McpServerTool, Description("Use when you need to confirm a mod only targets allowed official content.")]
    public static string ScanDlcDependencies(
        ServerData serverData,
        ProjectContext projectContext,
        [Description("Comma-separated DLC set to allow, for example 'Core,Biotech'")] string? allowedDlcs = null,
        [Description("Optional: specific mod package ID to scan")] string? modPackageId = null,
        [Description("Maximum findings to include per mod")] int maxFindingsPerMod = 12)
    {
        var allowedSet = ParseAllowedContentSet(allowedDlcs ?? projectContext.AllowedDlcs);
        var mods = GetTargetMods(serverData, modPackageId).ToList();
        if (mods.Count == 0)
        {
            return Serialize(new { error = "No matching non-core, non-DLC mods were found" });
        }

        var findingsByMod = new List<object>();
        var totalFindings = 0;

        foreach (var mod in mods)
        {
            var findings = AnalyzeDlcDependenciesForMod(serverData, mod, allowedSet)
                .OrderByDescending(finding => SeverityRank(finding.Severity))
                .ThenBy(finding => finding.Kind, StringComparer.OrdinalIgnoreCase)
                .ToList();

            totalFindings += findings.Count;

            findingsByMod.Add(new
            {
                mod = new { packageId = mod.PackageId, name = mod.Name },
                findingCount = findings.Count,
                findings = findings.Take(Math.Max(1, maxFindingsPerMod)).Select(finding => new
                {
                    finding.Severity,
                    finding.Kind,
                    finding.ForbiddenContent,
                    finding.Message,
                    finding.DefName,
                    finding.FilePath,
                    finding.XPath,
                    finding.Reference
                }).ToList()
            });
        }

        return Serialize(new
        {
            allowedContent = allowedSet.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            checkedMods = mods.Count,
            totalFindings,
            findingsByMod
        });
    }

    [McpServerTool, Description("Use when you want a compact audit of one mod, def, def type, or path.")]
    public static string AuditScope(
        ServerData serverData,
        [Description("Scope type: mod, def, def_type, or path")] string scopeType,
        [Description("Scope value for the selected scope type")] string scopeValue,
        [Description("Minimum severity to include: info, warning, or error")] string severity = "warning",
        [Description("Maximum findings to return")] int maxFindings = 40)
    {
        if (string.IsNullOrWhiteSpace(scopeType) || string.IsNullOrWhiteSpace(scopeValue))
        {
            return Serialize(new { error = "Both scopeType and scopeValue are required" });
        }

        var normalizedScopeType = NormalizeScopeType(scopeType);
        if (normalizedScopeType == null)
        {
            return Serialize(new { error = $"Unsupported scopeType '{scopeType}'. Use mod, def, def_type, or path." });
        }

        var scopedMods = GetScopedMods(serverData, normalizedScopeType, scopeValue).ToList();
        var scopedDefs = GetScopedDefs(serverData, normalizedScopeType, scopeValue).ToList();
        var scopedPatches = GetScopedPatches(serverData, normalizedScopeType, scopeValue, scopedDefs).ToList();

        if (scopedMods.Count == 0 && scopedDefs.Count == 0 && scopedPatches.Count == 0)
        {
            return Serialize(new { error = $"Scope '{scopeType}:{scopeValue}' did not match any loaded content" });
        }

        var findings = CollectAuditFindings(serverData, scopedMods, scopedDefs, scopedPatches)
            .Where(finding => MeetsSeverityThreshold(finding.Severity, severity))
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Code, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxFindings))
            .ToList();

        return Serialize(new
        {
            scope = new
            {
                type = normalizedScopeType,
                value = scopeValue,
                matchedMods = scopedMods.Select(mod => mod.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                matchedDefs = scopedDefs.Count,
                matchedPatches = scopedPatches.Count
            },
            summary = new
            {
                errors = findings.Count(finding => string.Equals(finding.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                warnings = findings.Count(finding => string.Equals(finding.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
                infos = findings.Count(finding => string.Equals(finding.Severity, "info", StringComparison.OrdinalIgnoreCase))
            },
            findings = findings.Select(finding => new
            {
                finding.Severity,
                finding.Code,
                finding.Message,
                finding.ModPackageId,
                finding.DefName,
                finding.FilePath,
                finding.XPath,
                finding.Suggestion
            }).ToList()
        });
    }

    [McpServerTool, Description("Use when you want clustered XPath conflicts instead of raw patch lists.")]
    public static string TriagePatchConflicts(
        ServerData serverData,
        [Description("Optional: only include hotspots involving this mod package ID")] string? modPackageId = null,
        [Description("Minimum severity to include: info, warning, or error")] string severity = "warning",
        [Description("Maximum hotspots to return")] int maxResults = 25)
    {
        if (!string.IsNullOrWhiteSpace(modPackageId) && ResolveMod(serverData, modPackageId) == null)
        {
            return Serialize(new { error = $"Mod '{modPackageId}' was not found" });
        }

        var hotspots = BuildPatchHotspots(serverData, modPackageId)
            .Where(hotspot => MeetsSeverityThreshold(hotspot.Severity, severity))
            .OrderByDescending(hotspot => SeverityRank(hotspot.Severity))
            .ThenByDescending(hotspot => hotspot.PatchCount)
            .Take(Math.Max(1, maxResults))
            .ToList();

        return Serialize(new
        {
            filteredMod = modPackageId,
            summary = new
            {
                hotspots = hotspots.Count,
                errorHotspots = hotspots.Count(hotspot => string.Equals(hotspot.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                warningHotspots = hotspots.Count(hotspot => string.Equals(hotspot.Severity, "warning", StringComparison.OrdinalIgnoreCase))
            },
            hotspots = hotspots.Select(hotspot => new
            {
                hotspot.Severity,
                hotspot.XPath,
                hotspot.PatchCount,
                hotspot.Operations,
                hotspot.Mods,
                hotspot.Files,
                hotspot.TargetDefs
            }).ToList()
        });
    }

    [McpServerTool, Description("Use when you want a compact view of overrides, references, patches, and broken links for a mod.")]
    public static string ContentCoverageReport(
        ServerData serverData,
        [Description("Optional: specific mod package ID to scope to")] string? modPackageId = null,
        [Description("Optional: only include defs of this type")] string? defType = null,
        [Description("Maximum examples to return per section")] int maxExamples = 12)
    {
        var targetDefs = GetCoverageDefs(serverData, modPackageId, defType).ToList();
        if (targetDefs.Count == 0)
        {
            return Serialize(new { error = "No matching definitions were found for the requested coverage scope" });
        }

        var targetDefNames = targetDefs.Select(def => def.DefName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var incomingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in serverData.Defs.Values)
        {
            foreach (var reference in GetResolvedReferences(def, serverData))
            {
                if (targetDefNames.Contains(reference.ToDef) &&
                    !string.Equals(reference.FromDef, reference.ToDef, StringComparison.OrdinalIgnoreCase))
                {
                    incomingCounts[reference.ToDef] = incomingCounts.GetValueOrDefault(reference.ToDef) + 1;
                }
            }
        }

        var patchCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var patch in serverData.GlobalPatches)
        {
            foreach (var targetDefName in ExtractDefNamesFromXPath(patch.XPath))
            {
                if (targetDefNames.Contains(targetDefName))
                {
                    patchCounts[targetDefName] = patchCounts.GetValueOrDefault(targetDefName) + 1;
                }
            }
        }

        var unresolvedByDef = targetDefs
            .Select(def => new
            {
                def.DefName,
                def.Type,
                def.FilePath,
                Unresolved = GetUnresolvedReferences(def, serverData).Take(5).ToList()
            })
            .Where(item => item.Unresolved.Count > 0)
            .ToList();

        var overrideConflicts = serverData.Conflicts
            .Where(conflict => conflict.Type == ConflictType.Override &&
                               !string.IsNullOrWhiteSpace(conflict.DefName) &&
                               targetDefNames.Contains(conflict.DefName!))
            .ToList();

        var unreferencedDefs = targetDefs
            .Where(def => !def.Abstract &&
                          incomingCounts.GetValueOrDefault(def.DefName) == 0 &&
                          patchCounts.GetValueOrDefault(def.DefName) == 0)
            .Take(Math.Max(1, maxExamples))
            .Select(def => new
            {
                def.DefName,
                def.Type,
                mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                def.FilePath
            })
            .ToList();

        return Serialize(new
        {
            scope = new
            {
                modPackageId,
                defType,
                matchedDefs = targetDefs.Count
            },
            summary = new
            {
                totalDefs = targetDefs.Count,
                referencedByOthers = targetDefs.Count(def => incomingCounts.GetValueOrDefault(def.DefName) > 0),
                patchedByXml = targetDefs.Count(def => patchCounts.GetValueOrDefault(def.DefName) > 0),
                unreferenced = unreferencedDefs.Count,
                overridden = overrideConflicts.Count,
                defsWithBrokenRefs = unresolvedByDef.Count,
                missingParents = targetDefs.Count(def => !string.IsNullOrWhiteSpace(def.Parent) && FindDefByName(serverData, def.Parent) == null)
            },
            byType = targetDefs
                .GroupBy(def => def.Type, StringComparer.OrdinalIgnoreCase)
                .Select(group => new { type = group.Key, count = group.Count() })
                .OrderByDescending(group => group.count)
                .Take(12)
                .ToList(),
            sampleUnreferenced = unreferencedDefs,
            sampleBrokenRefs = unresolvedByDef
                .Take(Math.Max(1, maxExamples))
                .Select(item => new
                {
                    item.DefName,
                    item.Type,
                    item.FilePath,
                    unresolved = item.Unresolved.Select(reference => new { reference.Value, reference.Path }).ToList()
                })
                .ToList(),
            sampleOverrides = overrideConflicts
                .Take(Math.Max(1, maxExamples))
                .Select(conflict => new
                {
                    conflict.DefName,
                    severity = ToSeverity(conflict.Severity),
                    conflict.Description,
                    mods = conflict.Mods.Select(mod => new { packageId = mod.PackageId, name = mod.Name }).ToList()
                })
                .ToList()
        });
    }

    [McpServerTool, Description("Use when you want a release-readiness verdict for one mod or all loaded custom mods.")]
    public static string ModReadyCheck(
        ServerData serverData,
        ProjectContext projectContext,
        [Description("Optional: specific mod package ID to evaluate")] string? modPackageId = null,
        [Description("Comma-separated DLC compatibility target, for example 'Core,Biotech'")] string? allowedDlcs = null,
        [Description("Optional: Player.log path for runtime issue inclusion")] string? logPath = null,
        [Description("Maximum issue examples per check")] int maxIssues = 8)
    {
        var allowedSet = ParseAllowedContentSet(allowedDlcs ?? projectContext.AllowedDlcs);
        var targetMods = GetTargetMods(serverData, modPackageId).ToList();
        if (targetMods.Count == 0)
        {
            return Serialize(new { error = "No matching non-core, non-DLC mods were found" });
        }

        List<LogIncident> logIncidents = [];
        var resolvedLogPath = logPath ?? projectContext.LogPath;
        if (!string.IsNullOrWhiteSpace(resolvedLogPath) && File.Exists(resolvedLogPath))
        {
            logIncidents = AnalyzePlayerLog(serverData, resolvedLogPath, null);
        }

        var modStatuses = targetMods.Select(mod =>
        {
            var dependencyConflicts = serverData.Conflicts
                .Where(conflict => conflict.Mods.Any(conflictMod => string.Equals(conflictMod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase)) &&
                                   (conflict.Type == ConflictType.MissingDependency ||
                                    conflict.Type == ConflictType.CircularDependency ||
                                    (conflict.Type == ConflictType.Override &&
                                     conflict.Description.Contains("incompatible", StringComparison.OrdinalIgnoreCase))))
                .ToList();

            var unresolvedRefs = GetDefsForMod(serverData, mod)
                .Select(def => new
                {
                    def.DefName,
                    def.Type,
                    def.FilePath,
                    Unresolved = GetUnresolvedReferences(def, serverData).Take(3).ToList()
                })
                .Where(item => item.Unresolved.Count > 0)
                .Take(Math.Max(1, maxIssues))
                .ToList();

            var dlcFindings = AnalyzeDlcDependenciesForMod(serverData, mod, allowedSet)
                .Take(Math.Max(1, maxIssues))
                .ToList();

            var patchHotspots = BuildPatchHotspots(serverData, mod.PackageId)
                .Take(Math.Max(1, maxIssues))
                .ToList();

            var modLogGroups = logIncidents
                .Where(incident => incident.MentionedMods.Contains(mod.PackageId, StringComparer.OrdinalIgnoreCase) ||
                                   incident.MentionedMods.Contains(mod.Name, StringComparer.OrdinalIgnoreCase))
                .GroupBy(incident => incident.Signature, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    category = group.First().Category,
                    headline = group.First().Headline,
                    count = group.Count()
                })
                .OrderByDescending(group => group.count)
                .Take(Math.Max(1, maxIssues))
                .ToList();

            var blockerCount = dependencyConflicts.Count(conflict => conflict.Severity == ConflictSeverity.Error) + dlcFindings.Count;
            var warningCount = dependencyConflicts.Count(conflict => conflict.Severity != ConflictSeverity.Error) +
                               unresolvedRefs.Count +
                               patchHotspots.Count +
                               modLogGroups.Count;

            var status = blockerCount > 0 ? "blocked" : warningCount > 0 ? "warning" : "ready";

            var nextSteps = new List<string>();
            if (dependencyConflicts.Count > 0) nextSteps.Add("Fix dependency and load-order issues first.");
            if (dlcFindings.Count > 0) nextSteps.Add("Remove or gate forbidden DLC references for the target compatibility set.");
            if (unresolvedRefs.Count > 0) nextSteps.Add("Correct broken DefName references before shipping.");
            if (patchHotspots.Count > 0) nextSteps.Add("Review XPath hotspots for load-order sensitivity.");
            if (modLogGroups.Count > 0) nextSteps.Add("Re-test after clearing the log and confirm the runtime issues are still reproducible.");

            return new
            {
                mod = new { packageId = mod.PackageId, name = mod.Name },
                status,
                blockerCount,
                warningCount,
                checks = new object[]
                {
                    new
                    {
                        name = "dependencies",
                        status = dependencyConflicts.Any(conflict => conflict.Severity == ConflictSeverity.Error) ? "blocked" : dependencyConflicts.Count > 0 ? "warning" : "ready",
                        issueCount = dependencyConflicts.Count,
                        examples = dependencyConflicts.Take(Math.Max(1, maxIssues)).Select(conflict => new
                        {
                            severity = ToSeverity(conflict.Severity),
                            conflict.Description,
                            conflict.Resolution
                        }).ToList()
                    },
                    new
                    {
                        name = "dlc_compatibility",
                        status = dlcFindings.Count > 0 ? "blocked" : "ready",
                        issueCount = dlcFindings.Count,
                        examples = dlcFindings.Select(finding => new
                        {
                            finding.Severity,
                            finding.Kind,
                            finding.ForbiddenContent,
                            finding.Message,
                            finding.DefName,
                            finding.FilePath
                        }).ToList()
                    },
                    new
                    {
                        name = "references",
                        status = unresolvedRefs.Count > 0 ? "warning" : "ready",
                        issueCount = unresolvedRefs.Count,
                        examples = unresolvedRefs.Select(item => new
                        {
                            item.DefName,
                            item.Type,
                            item.FilePath,
                            unresolved = item.Unresolved.Select(reference => new { reference.Value, reference.Path }).ToList()
                        }).ToList()
                    },
                    new
                    {
                        name = "patch_hotspots",
                        status = patchHotspots.Count > 0 ? "warning" : "ready",
                        issueCount = patchHotspots.Count,
                        examples = patchHotspots.Select(hotspot => new
                        {
                            hotspot.Severity,
                            hotspot.XPath,
                            hotspot.PatchCount,
                            hotspot.TargetDefs
                        }).ToList()
                    },
                    new
                    {
                        name = "runtime_log",
                        status = modLogGroups.Count > 0 ? "warning" : string.IsNullOrWhiteSpace(resolvedLogPath) ? "skipped" : "ready",
                        issueCount = modLogGroups.Count,
                        examples = modLogGroups
                    }
                },
                nextSteps = nextSteps.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        }).ToList();

        return Serialize(new
        {
            allowedContent = allowedSet.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList(),
            logPath = !string.IsNullOrWhiteSpace(resolvedLogPath) && File.Exists(resolvedLogPath) ? resolvedLogPath : null,
            summary = new
            {
                checkedMods = modStatuses.Count,
                ready = modStatuses.Count(status => string.Equals(status.status, "ready", StringComparison.OrdinalIgnoreCase)),
                warnings = modStatuses.Count(status => string.Equals(status.status, "warning", StringComparison.OrdinalIgnoreCase)),
                blocked = modStatuses.Count(status => string.Equals(status.status, "blocked", StringComparison.OrdinalIgnoreCase))
            },
            modStatuses
        });
    }

    [McpServerTool, Description("Use when setup is unclear and you need to verify paths, config, git state, and server readiness.")]
    public static string Doctor(
        ProjectContext projectContext,
        ServerData serverData,
        [Description("Optional: Player.log path override")] string? logPath = null)
    {
        var checks = new List<object>();
        var errorCount = 0;
        var warningCount = 0;
        var resolvedLogPath = logPath ?? projectContext.LogPath;

        void AddCheck(string name, string status, string message, object? details = null)
        {
            if (string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            {
                errorCount++;
            }
            else if (string.Equals(status, "warning", StringComparison.OrdinalIgnoreCase))
            {
                warningCount++;
            }

            checks.Add(new
            {
                name,
                status,
                message,
                details
            });
        }

        AddCheck(
            "project_config",
            projectContext.HasProjectConfig ? "ready" : "warning",
            projectContext.HasProjectConfig
                ? $"Using project config at '{projectContext.ConfigPath}'"
                : "No .rimworld-modder-mcp.json was found. The server will rely on CLI arguments only.",
            new
            {
                configPath = projectContext.ConfigPath,
                projectRoot = projectContext.ProjectRoot
            });

        AddCheck(
            "project_root",
            Directory.Exists(projectContext.ProjectRoot) ? "ready" : "error",
            Directory.Exists(projectContext.ProjectRoot)
                ? $"Project root exists: '{projectContext.ProjectRoot}'"
                : $"Project root '{projectContext.ProjectRoot}' does not exist");

        if (string.IsNullOrWhiteSpace(projectContext.RimworldPath))
        {
            AddCheck("rimworld_path", "error", "No RimWorld path is configured.");
        }
        else
        {
            var dataDir = Path.Combine(projectContext.RimworldPath, "Data");
            AddCheck(
                "rimworld_path",
                Directory.Exists(dataDir) ? "ready" : "error",
                Directory.Exists(dataDir)
                    ? $"RimWorld data directory found at '{dataDir}'"
                    : $"RimWorld data directory was not found under '{projectContext.RimworldPath}'",
                new
                {
                    rimworldPath = projectContext.RimworldPath,
                    dataDirectory = dataDir
                });
        }

        if (projectContext.ModDirs.Count == 0)
        {
            AddCheck("mod_dirs", "warning", "No mod directories are configured.");
        }
        else
        {
            var existing = projectContext.ModDirs.Where(Directory.Exists).ToList();
            var missing = projectContext.ModDirs.Where(path => !Directory.Exists(path)).ToList();
            AddCheck(
                "mod_dirs",
                missing.Count > 0 ? "warning" : "ready",
                missing.Count > 0
                    ? $"{missing.Count} configured mod directories were not found."
                    : $"All {existing.Count} configured mod directories exist.",
                new
                {
                    existing,
                    missing
                });
        }

        if (!string.IsNullOrWhiteSpace(projectContext.ModsConfigPath))
        {
            AddCheck(
                "mods_config",
                File.Exists(projectContext.ModsConfigPath) ? "ready" : "warning",
                File.Exists(projectContext.ModsConfigPath)
                    ? $"ModsConfig.xml found at '{projectContext.ModsConfigPath}'"
                    : $"ModsConfig.xml was not found at '{projectContext.ModsConfigPath}'");
        }

        if (!string.IsNullOrWhiteSpace(resolvedLogPath))
        {
            AddCheck(
                "player_log",
                File.Exists(resolvedLogPath) ? "ready" : "warning",
                File.Exists(resolvedLogPath)
                    ? $"Player log found at '{resolvedLogPath}'"
                    : $"Player log was not found at '{resolvedLogPath}'");
        }
        else
        {
            AddCheck("player_log", "warning", "No Player.log path is configured.");
        }

        var gitMetadataPath = Path.Combine(projectContext.ProjectRoot, ".git");
        AddCheck(
            "git_repo",
            Directory.Exists(gitMetadataPath) || File.Exists(gitMetadataPath) ? "ready" : "warning",
            Directory.Exists(gitMetadataPath) || File.Exists(gitMetadataPath)
                ? "Project root appears to be a git repository."
                : "Project root does not appear to be a git repository.");

        AddCheck(
            "loaded_data",
            serverData.IsFullyLoaded ? "ready" : serverData.IsModsLoaded || serverData.IsDefsLoaded ? "warning" : "info",
            serverData.IsFullyLoaded
                ? "Server data is fully loaded."
                : "Server data is not fully loaded yet.",
            new
            {
                isModsLoaded = serverData.IsModsLoaded,
                isDefsLoaded = serverData.IsDefsLoaded,
                isConflictsAnalyzed = serverData.IsConflictsAnalyzed,
                modCount = serverData.Mods.Count,
                defCount = serverData.Defs.Count,
                patchCount = serverData.GlobalPatches.Count,
                conflictCount = serverData.Conflicts.Count
            });

        var status = errorCount > 0 ? "error" : warningCount > 0 ? "warning" : "ready";

        return Serialize(new
        {
            status,
            summary = new
            {
                errors = errorCount,
                warnings = warningCount,
                hasProjectConfig = projectContext.HasProjectConfig,
                projectRoot = projectContext.ProjectRoot,
                rimworldPath = projectContext.RimworldPath,
                modDirCount = projectContext.ModDirs.Count,
                logPath = projectContext.LogPath,
                allowedDlcs = projectContext.AllowedDlcs
            },
            checks
        });
    }

    [McpServerTool, Description("Use when you only want audit findings for files changed in git or an explicit path list.")]
    public static string AuditChangedFiles(
        ServerData serverData,
        ProjectContext projectContext,
        [Description("Optional git base ref, for example origin/main")] string? baseRef = null,
        [Description("Optional explicit file paths to audit instead of using git")] string[]? paths = null,
        [Description("Minimum severity to include: info, warning, or error")] string severity = "warning",
        [Description("Maximum findings to return")] int maxResults = 40)
    {
        var changedFiles = ResolveChangedFiles(projectContext, baseRef, paths);
        if (changedFiles.Count == 0)
        {
            return Serialize(new { error = "No changed files were found for the requested scope." });
        }

        var scopedDefs = GetChangedDefs(serverData, changedFiles).ToList();
        var scopedPatches = GetChangedPatches(serverData, changedFiles).ToList();
        var scopedMods = GetChangedMods(serverData, changedFiles)
            .Concat(scopedDefs.Select(def => def.Mod))
            .Concat(scopedPatches.Select(patch => patch.Mod))
            .DistinctBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var findings = CollectAuditFindings(serverData, scopedMods, scopedDefs, scopedPatches)
            .Where(finding => MeetsSeverityThreshold(finding.Severity, severity))
            .OrderByDescending(finding => SeverityRank(finding.Severity))
            .ThenBy(finding => finding.Code, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxResults))
            .ToList();

        var matchedPaths = scopedDefs.Select(def => NormalizePath(ResolveDefFilePath(def)))
            .Concat(scopedPatches.Select(patch => NormalizePath(patch.FilePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return Serialize(new
        {
            projectContext.ProjectRoot,
            changeSource = paths != null && paths.Length > 0 ? "explicit_paths" : string.IsNullOrWhiteSpace(baseRef) ? "git_status" : "git_diff",
            baseRef,
            summary = new
            {
                changedFiles = changedFiles.Count,
                matchedMods = scopedMods.Count,
                matchedDefs = scopedDefs.Count,
                matchedPatches = scopedPatches.Count,
                findings = findings.Count
            },
            changedFiles = changedFiles.Select(path => new
            {
                path,
                kind = ClassifyChangedPath(path),
                matched = matchedPaths.Contains(NormalizePath(path))
            }).ToList(),
            unmatchedChangedFiles = changedFiles
                .Where(path => !matchedPaths.Contains(NormalizePath(path)))
                .Take(20)
                .ToList(),
            findings = findings.Select(finding => new
            {
                finding.Severity,
                finding.Code,
                finding.Message,
                finding.ModPackageId,
                finding.DefName,
                finding.FilePath,
                finding.XPath,
                finding.Suggestion
            }).ToList()
        });
    }

    [McpServerTool, Description("Use when you want pre-commit validation for only changed defs and patches.")]
    public static string ValidateChangedContent(
        ServerData serverData,
        ProjectContext projectContext,
        [Description("Optional git base ref, for example origin/main")] string? baseRef = null,
        [Description("Optional explicit file paths to validate instead of using git")] string[]? paths = null,
        [Description("Optional DLC compatibility target override")] string? allowedDlcs = null,
        [Description("Optional Player.log path override")] string? logPath = null,
        [Description("Maximum examples to return per section")] int maxResults = 25)
    {
        var changedFiles = ResolveChangedFiles(projectContext, baseRef, paths);
        if (changedFiles.Count == 0)
        {
            return Serialize(new { error = "No changed files were found for the requested scope." });
        }

        var changedDefs = GetChangedDefs(serverData, changedFiles).ToList();
        var changedPatches = GetChangedPatches(serverData, changedFiles).ToList();
        var affectedMods = GetChangedMods(serverData, changedFiles)
            .Concat(changedDefs.Select(def => def.Mod))
            .Concat(changedPatches.Select(patch => patch.Mod))
            .DistinctBy(mod => mod.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var hotspotsByXPath = BuildAllPatchHotspots(serverData, null)
            .ToDictionary(hotspot => NormalizeXPath(hotspot.XPath), StringComparer.OrdinalIgnoreCase);

        var defChecks = changedDefs
            .Take(Math.Max(1, maxResults))
            .Select(def =>
            {
                var issues = new List<object>();
                var hasError = false;
                try
                {
                    _ = XDocument.Parse(def.Content.ToString());
                }
                catch (Exception ex)
                {
                    hasError = true;
                    issues.Add(new { severity = "error", code = "xml_parse", message = ex.Message });
                }

                if (!string.IsNullOrWhiteSpace(def.Parent) && FindDefByName(serverData, def.Parent) == null)
                {
                    issues.Add(new { severity = "warning", code = "missing_parent", message = $"Missing parent '{def.Parent}'" });
                }

                foreach (var reference in GetUnresolvedReferences(def, serverData).Take(5))
                {
                    issues.Add(new { severity = "warning", code = "unresolved_reference", message = $"Missing reference '{reference.Value}'", reference.Path });
                }

                var status = hasError ? "blocked" : issues.Count > 0 ? "warning" : "ready";

                return new
                {
                    def.DefName,
                    def.Type,
                    mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                    def.FilePath,
                    status,
                    issues
                };
            })
            .ToList();

        var patchChecks = changedPatches
            .Take(Math.Max(1, maxResults))
            .Select(patch =>
            {
                var targetDefs = ExtractDefNamesFromXPath(patch.XPath);
                var missingTargets = targetDefs
                    .Where(targetDef => FindDefByName(serverData, targetDef) == null)
                    .Take(6)
                    .ToList();
                var hotspot = hotspotsByXPath.GetValueOrDefault(NormalizeXPath(patch.XPath));
                var status = missingTargets.Count > 0
                    ? "warning"
                    : hotspot != null && SeverityRank(hotspot.Severity) >= SeverityRank("warning")
                        ? "warning"
                        : "ready";

                return new
                {
                    patch.FilePath,
                    mod = new { packageId = patch.Mod.PackageId, name = patch.Mod.Name },
                    operation = patch.Operation.ToString(),
                    patch.XPath,
                    status,
                    targetDefs = targetDefs.Take(8).ToList(),
                    missingTargets,
                    hotspot = hotspot == null ? null : new
                    {
                        hotspot.Severity,
                        hotspot.PatchCount,
                        hotspot.Mods
                    }
                };
            })
            .ToList();

        var dlcFindings = affectedMods
            .SelectMany(mod => AnalyzeDlcDependenciesForMod(serverData, mod, ParseAllowedContentSet(allowedDlcs ?? projectContext.AllowedDlcs)))
            .Take(Math.Max(1, maxResults))
            .ToList();

        var resolvedLogPath = logPath ?? projectContext.LogPath;
        var logGroups = new List<object>();
        if (!string.IsNullOrWhiteSpace(resolvedLogPath) && File.Exists(resolvedLogPath))
        {
            var incidents = AnalyzePlayerLog(serverData, resolvedLogPath, null);
            var affectedModNames = affectedMods.SelectMany(mod => new[] { mod.PackageId, mod.Name }).ToHashSet(StringComparer.OrdinalIgnoreCase);
            logGroups = incidents
                .Where(incident => incident.MentionedMods.Any(affectedModNames.Contains))
                .GroupBy(incident => incident.Signature, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    category = group.First().Category,
                    headline = group.First().Headline,
                    count = group.Count()
                })
                .OrderByDescending(group => group.count)
                .Take(Math.Max(1, maxResults))
                .Cast<object>()
                .ToList();
        }

        return Serialize(new
        {
            projectContext.ProjectRoot,
            changeSource = paths != null && paths.Length > 0 ? "explicit_paths" : string.IsNullOrWhiteSpace(baseRef) ? "git_status" : "git_diff",
            baseRef,
            summary = new
            {
                changedFiles = changedFiles.Count,
                changedDefs = changedDefs.Count,
                changedPatches = changedPatches.Count,
                affectedMods = affectedMods.Count,
                blockedDefs = defChecks.Count(check => string.Equals(check.status, "blocked", StringComparison.OrdinalIgnoreCase)),
                warningDefs = defChecks.Count(check => string.Equals(check.status, "warning", StringComparison.OrdinalIgnoreCase)),
                warningPatches = patchChecks.Count(check => string.Equals(check.status, "warning", StringComparison.OrdinalIgnoreCase)),
                dlcFindings = dlcFindings.Count,
                runtimeLogGroups = logGroups.Count
            },
            changedFiles = changedFiles.Select(path => new { path, kind = ClassifyChangedPath(path) }).ToList(),
            defChecks,
            patchChecks,
            dlcFindings = dlcFindings.Select(finding => new
            {
                finding.Severity,
                finding.Kind,
                finding.ForbiddenContent,
                finding.Message,
                finding.DefName,
                finding.FilePath
            }).ToList(),
            runtimeLogGroups = logGroups
        });
    }

    [McpServerTool, Description("Use when you want current vs previous Player.log differences grouped into new, resolved, and regressed issues.")]
    public static string ComparePlayerLogs(
        ServerData serverData,
        [Description("Path to the newer Player.log file")] string logPath,
        [Description("Path to the older Player.log file")] string otherLogPath,
        [Description("Optional: only include incidents clearly tied to this mod package ID")] string? modPackageId = null,
        [Description("Maximum grouped issue buckets to return")] int maxResults = 15)
    {
        if (!File.Exists(logPath))
        {
            return Serialize(new { error = $"Log file '{logPath}' was not found" });
        }

        if (!File.Exists(otherLogPath))
        {
            return Serialize(new { error = $"Log file '{otherLogPath}' was not found" });
        }

        ModInfo? targetMod = null;
        if (!string.IsNullOrWhiteSpace(modPackageId) && serverData.Mods.Count > 0)
        {
            targetMod = ResolveMod(serverData, modPackageId);
            if (targetMod == null)
            {
                return Serialize(new { error = $"Mod '{modPackageId}' was not found" });
            }
        }

        var current = GroupLogIncidents(AnalyzePlayerLog(serverData, logPath, targetMod));
        var previous = GroupLogIncidents(AnalyzePlayerLog(serverData, otherLogPath, targetMod));

        var newGroups = current.Values
            .Where(group => !previous.ContainsKey(group.Signature))
            .OrderByDescending(group => group.Count)
            .Take(Math.Max(1, maxResults))
            .ToList();

        var resolvedGroups = previous.Values
            .Where(group => !current.ContainsKey(group.Signature))
            .OrderByDescending(group => group.Count)
            .Take(Math.Max(1, maxResults))
            .ToList();

        var regressedGroups = current.Values
            .Where(group => previous.TryGetValue(group.Signature, out var previousGroup) && group.Count > previousGroup.Count)
            .OrderByDescending(group => group.Count)
            .Take(Math.Max(1, maxResults))
            .Select(group => new
            {
                group.Category,
                group.Signature,
                group.Headline,
                currentCount = group.Count,
                previousCount = previous[group.Signature].Count
            })
            .ToList();

        object? filteredMod = targetMod == null
            ? modPackageId
            : new { packageId = targetMod.PackageId, name = targetMod.Name };

        return Serialize(new
        {
            currentLogPath = logPath,
            previousLogPath = otherLogPath,
            filteredMod,
            summary = new
            {
                currentGroups = current.Count,
                previousGroups = previous.Count,
                newGroups = newGroups.Count,
                resolvedGroups = resolvedGroups.Count,
                regressedGroups = regressedGroups.Count
            },
            newGroups,
            resolvedGroups,
            regressedGroups
        });
    }

    [McpServerTool, Description("Use when you want the busiest or riskiest XML patch targets in the loadout.")]
    public static string FindPatchHotspots(
        ServerData serverData,
        [Description("Optional: only include hotspots involving this mod package ID")] string? modPackageId = null,
        [Description("Minimum severity to include: info, warning, or error")] string severity = "info",
        [Description("Maximum hotspots to return")] int maxResults = 25)
    {
        if (!string.IsNullOrWhiteSpace(modPackageId) && ResolveMod(serverData, modPackageId) == null)
        {
            return Serialize(new { error = $"Mod '{modPackageId}' was not found" });
        }

        var hotspots = BuildAllPatchHotspots(serverData, modPackageId)
            .Where(hotspot => MeetsSeverityThreshold(hotspot.Severity, severity))
            .OrderByDescending(hotspot => hotspot.Mods.Count)
            .ThenByDescending(hotspot => hotspot.PatchCount)
            .Take(Math.Max(1, maxResults))
            .ToList();

        return Serialize(new
        {
            filteredMod = modPackageId,
            summary = new
            {
                hotspots = hotspots.Count,
                crossModHotspots = hotspots.Count(hotspot => hotspot.Mods.Count > 1),
                repeatedSingleModHotspots = hotspots.Count(hotspot => hotspot.Mods.Count == 1)
            },
            hotspots = hotspots.Select(hotspot => new
            {
                hotspot.Severity,
                hotspot.XPath,
                hotspot.PatchCount,
                distinctMods = hotspot.Mods.Count,
                hotspot.Operations,
                hotspot.Mods,
                hotspot.Files,
                hotspot.TargetDefs
            }).ToList()
        });
    }

    [McpServerTool, Description("Use when a missing def reference needs likely causes and nearby loaded alternatives.")]
    public static string BrokenReferenceExplainer(
        ServerData serverData,
        [Description("Broken reference or target DefName to explain")] string reference,
        [Description("Optional source definition name containing the broken reference")] string? defName = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return Serialize(new { error = "reference is required" });
        }

        var sourceDefs = new List<RimWorldDef>();
        if (!string.IsNullOrWhiteSpace(defName))
        {
            var sourceDef = FindDefByName(serverData, defName);
            if (sourceDef == null)
            {
                return Serialize(new { error = $"Definition '{defName}' was not found" });
            }

            sourceDefs.Add(sourceDef);
        }
        else
        {
            sourceDefs = serverData.Defs.Values
                .Where(def => GetUnresolvedReferences(def, serverData).Any(entry => string.Equals(entry.Value, reference, StringComparison.OrdinalIgnoreCase)))
                .Take(12)
                .ToList();
        }

        var observations = sourceDefs
            .Select(def => new
            {
                def.DefName,
                def.Type,
                mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                def.FilePath,
                matches = GetUnresolvedReferences(def, serverData)
                    .Where(entry => string.Equals(entry.Value, reference, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => new { entry.Value, entry.Path })
                    .ToList()
            })
            .Where(item => item.matches.Count > 0)
            .ToList();

        var exactCaseInsensitiveMatch = serverData.Defs.Values
            .Concat(serverData.AbstractDefs.Values)
            .FirstOrDefault(def => string.Equals(def.DefName, reference, StringComparison.OrdinalIgnoreCase));

        var similarDefs = FindSimilarDefCandidates(serverData, reference)
            .Take(8)
            .Select(candidate => new
            {
                candidate.DefName,
                candidate.Type,
                mod = new { packageId = candidate.Mod.PackageId, name = candidate.Mod.Name },
                candidate.FilePath
            })
            .ToList();

        var likelyCauses = new List<object>();
        if (exactCaseInsensitiveMatch != null && !string.Equals(exactCaseInsensitiveMatch.DefName, reference, StringComparison.Ordinal))
        {
            likelyCauses.Add(new
            {
                kind = "case_mismatch",
                message = $"A loaded def named '{exactCaseInsensitiveMatch.DefName}' exists with different casing."
            });
        }

        if (similarDefs.Count > 0)
        {
            likelyCauses.Add(new
            {
                kind = "renamed_or_misspelled",
                message = "Loaded defs with similar names exist. The reference may be stale, renamed, or misspelled."
            });
        }

        if (sourceDefs.Any(def =>
                serverData.Conflicts.Any(conflict =>
                    conflict.Type == ConflictType.MissingDependency &&
                    conflict.Mods.Any(mod => string.Equals(mod.PackageId, def.Mod.PackageId, StringComparison.OrdinalIgnoreCase)))))
        {
            likelyCauses.Add(new
            {
                kind = "missing_dependency",
                message = "One or more source mods already have missing dependency conflicts. The reference may belong to a dependency that is not loaded."
            });
        }

        if (observations.Count == 0)
        {
            likelyCauses.Add(new
            {
                kind = "not_observed",
                message = "No loaded def currently exposes this exact unresolved reference value."
            });
        }

        var nextChecks = new List<string>();
        if (exactCaseInsensitiveMatch != null && !string.Equals(exactCaseInsensitiveMatch.DefName, reference, StringComparison.Ordinal))
        {
            nextChecks.Add($"Update the reference to '{exactCaseInsensitiveMatch.DefName}'.");
        }

        if (similarDefs.Count > 0)
        {
            nextChecks.Add("Compare the reference against the nearest loaded alternatives and check for a rename.");
        }

        if (sourceDefs.Count > 0)
        {
            nextChecks.Add("Inspect the source def's dependencies and load-order constraints.");
        }

        return Serialize(new
        {
            reference,
            defName,
            summary = new
            {
                sourceDefs = observations.Count,
                similarDefs = similarDefs.Count,
                likelyCauses = likelyCauses.Count
            },
            observations,
            likelyCauses,
            similarDefs,
            nextChecks = nextChecks.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        });
    }

    [McpServerTool, Description("Use when you want search results limited to one mod, def, def type, or path.")]
    public static string ScopeSearch(
        ServerData serverData,
        [Description("Scope type: mod, def, def_type, or path")] string scopeType,
        [Description("Scope value for the selected scope type")] string scopeValue,
        [Description("Search term to match inside defs, patches, and mods")] string searchTerm,
        [Description("Optional def type filter for matched defs")] string? inType = null,
        [Description("Maximum matches per result section")] int maxResults = 25)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            return Serialize(new { error = "searchTerm is required" });
        }

        var normalizedScopeType = NormalizeScopeType(scopeType);
        if (normalizedScopeType == null)
        {
            return Serialize(new { error = $"Unsupported scopeType '{scopeType}'. Use mod, def, def_type, or path." });
        }

        var scopedMods = GetScopedMods(serverData, normalizedScopeType, scopeValue).ToList();
        var scopedDefs = GetScopedDefs(serverData, normalizedScopeType, scopeValue).ToList();
        var scopedPatches = GetScopedPatches(serverData, normalizedScopeType, scopeValue, scopedDefs).ToList();

        if (!string.IsNullOrWhiteSpace(inType))
        {
            scopedDefs = scopedDefs
                .Where(def => string.Equals(def.Type, inType, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        var defMatches = scopedDefs
            .Where(def =>
                def.DefName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                def.Type.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                ResolveDefFilePath(def).Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                def.Content.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(1, maxResults))
            .Select(def => new
            {
                def.DefName,
                def.Type,
                mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                filePath = ResolveDefFilePath(def)
            })
            .ToList();

        var patchMatches = scopedPatches
            .Where(patch =>
                patch.FilePath.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                patch.XPath.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                patch.Operation.ToString().Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                ExtractDefNamesFromXPath(patch.XPath).Any(defNameValue => defNameValue.Contains(searchTerm, StringComparison.OrdinalIgnoreCase)))
            .Take(Math.Max(1, maxResults))
            .Select(patch => new
            {
                patch.FilePath,
                mod = new { packageId = patch.Mod.PackageId, name = patch.Mod.Name },
                operation = patch.Operation.ToString(),
                patch.XPath
            })
            .ToList();

        var modMatches = scopedMods
            .Where(mod =>
                mod.PackageId.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                mod.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase) ||
                mod.Path.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
            .Take(Math.Max(1, maxResults))
            .Select(mod => new
            {
                mod.PackageId,
                mod.Name,
                mod.Path,
                mod.LoadOrder
            })
            .ToList();

        return Serialize(new
        {
            scope = new
            {
                type = normalizedScopeType,
                value = scopeValue,
                searchTerm,
                inType
            },
            summary = new
            {
                matchedMods = modMatches.Count,
                matchedDefs = defMatches.Count,
                matchedPatches = patchMatches.Count
            },
            modMatches,
            defMatches,
            patchMatches
        });
    }

    [McpServerTool, Description("Use when you want to estimate what moving a mod before or after another would affect.")]
    public static string LoadOrderImpactReport(
        ServerData serverData,
        [Description("Mod package ID to move")] string modPackageId,
        [Description("Simulate moving the mod before this package ID")] string? moveBeforeModPackageId = null,
        [Description("Simulate moving the mod after this package ID")] string? moveAfterModPackageId = null,
        [Description("Maximum impacted mods to return")] int maxResults = 20)
    {
        var mod = ResolveMod(serverData, modPackageId);
        if (mod == null)
        {
            return Serialize(new { error = $"Mod '{modPackageId}' was not found" });
        }

        if (string.IsNullOrWhiteSpace(moveBeforeModPackageId) == string.IsNullOrWhiteSpace(moveAfterModPackageId))
        {
            return Serialize(new { error = "Provide exactly one of moveBeforeModPackageId or moveAfterModPackageId." });
        }

        var anchorId = moveBeforeModPackageId ?? moveAfterModPackageId;
        var anchor = ResolveMod(serverData, anchorId);
        if (anchor == null)
        {
            return Serialize(new { error = $"Anchor mod '{anchorId}' was not found" });
        }

        var currentOrder = mod.LoadOrder;
        var proposedOrder = !string.IsNullOrWhiteSpace(moveBeforeModPackageId)
            ? anchor.LoadOrder - 0.5
            : anchor.LoadOrder + 0.5;

        var hotspotMap = BuildPatchHotspots(serverData, null)
            .Where(hotspot => hotspot.Mods.Contains(mod.PackageId, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var impactedMods = serverData.Mods.Values
            .Where(other => !string.Equals(other.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase))
            .Select(other =>
            {
                var reasons = new List<string>();
                var severity = "info";

                if (WouldFlipRelativeOrder(currentOrder, proposedOrder, other.LoadOrder))
                {
                    reasons.Add("Relative load order would flip.");
                }

                if (mod.Dependencies.Contains(other.PackageId, StringComparer.OrdinalIgnoreCase) && proposedOrder <= other.LoadOrder)
                {
                    severity = "blocked";
                    reasons.Add("This mod depends on the other mod and should still load after it.");
                }

                if (other.Dependencies.Contains(mod.PackageId, StringComparer.OrdinalIgnoreCase) && proposedOrder >= other.LoadOrder)
                {
                    severity = "blocked";
                    reasons.Add("The other mod depends on this mod and should load after it.");
                }

                if (mod.LoadBefore.Contains(other.PackageId, StringComparer.OrdinalIgnoreCase) && proposedOrder >= other.LoadOrder)
                {
                    severity = severity == "blocked" ? severity : "warning";
                    reasons.Add("About.xml requests that this mod load before the other mod.");
                }

                if (mod.LoadAfter.Contains(other.PackageId, StringComparer.OrdinalIgnoreCase) && proposedOrder <= other.LoadOrder)
                {
                    severity = severity == "blocked" ? severity : "warning";
                    reasons.Add("About.xml requests that this mod load after the other mod.");
                }

                var sharedConflicts = serverData.Conflicts
                    .Where(conflict => conflict.Mods.Any(conflictMod => string.Equals(conflictMod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase)) &&
                                       conflict.Mods.Any(conflictMod => string.Equals(conflictMod.PackageId, other.PackageId, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (sharedConflicts.Count > 0)
                {
                    severity = severity == "blocked" ? severity : "warning";
                    reasons.Add("These mods already participate in a known conflict.");
                }

                var sharedHotspots = hotspotMap
                    .Where(hotspot => hotspot.Mods.Contains(other.PackageId, StringComparer.OrdinalIgnoreCase))
                    .Take(3)
                    .ToList();

                if (sharedHotspots.Count > 0)
                {
                    severity = severity == "blocked" ? severity : "warning";
                    reasons.Add("These mods share XML patch hotspots.");
                }

                return new
                {
                    mod = new { other.PackageId, other.Name, other.LoadOrder },
                    severity,
                    reasons,
                    conflictCount = sharedConflicts.Count,
                    hotspotCount = sharedHotspots.Count
                };
            })
            .Where(item => item.reasons.Count > 0)
            .OrderByDescending(item => SeverityRank(item.severity))
            .ThenByDescending(item => item.conflictCount + item.hotspotCount)
            .Take(Math.Max(1, maxResults))
            .ToList();

        return Serialize(new
        {
            mod = new { mod.PackageId, mod.Name, mod.LoadOrder },
            proposal = new
            {
                action = !string.IsNullOrWhiteSpace(moveBeforeModPackageId) ? "move_before" : "move_after",
                anchor = new { anchor.PackageId, anchor.Name, anchor.LoadOrder },
                currentLoadOrder = currentOrder,
                proposedRelativePosition = proposedOrder
            },
            summary = new
            {
                impactedMods = impactedMods.Count,
                blocked = impactedMods.Count(item => string.Equals(item.severity, "blocked", StringComparison.OrdinalIgnoreCase)),
                warnings = impactedMods.Count(item => string.Equals(item.severity, "warning", StringComparison.OrdinalIgnoreCase))
            },
            impactedMods
        });
    }

    private static string Serialize(object value) => JsonSerializer.Serialize(value);

    private static IEnumerable<AuditFinding> CollectAuditFindings(
        ServerData serverData,
        IReadOnlyCollection<ModInfo> scopedMods,
        IReadOnlyCollection<RimWorldDef> scopedDefs,
        IReadOnlyCollection<PatchOperation> scopedPatches)
    {
        var findings = new List<AuditFinding>();

        foreach (var conflict in serverData.Conflicts.Where(conflict =>
                     conflict.Mods.Any(mod => scopedMods.Any(scopedMod => string.Equals(scopedMod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase))) ||
                     (!string.IsNullOrWhiteSpace(conflict.DefName) &&
                      scopedDefs.Any(def => string.Equals(def.DefName, conflict.DefName, StringComparison.OrdinalIgnoreCase)))))
        {
            findings.Add(new AuditFinding(
                ToSeverity(conflict.Severity),
                conflict.Type.ToString().ToLowerInvariant(),
                conflict.Description,
                conflict.Mods.FirstOrDefault()?.PackageId,
                conflict.DefName,
                null,
                conflict.XPath,
                conflict.Resolution));
        }

        foreach (var def in scopedDefs)
        {
            if (!string.IsNullOrWhiteSpace(def.Parent) && FindDefByName(serverData, def.Parent) == null)
            {
                findings.Add(new AuditFinding(
                    "warning",
                    "missing_parent",
                    $"Definition '{def.DefName}' inherits from missing parent '{def.Parent}'",
                    def.Mod.PackageId,
                    def.DefName,
                    def.FilePath,
                    null,
                    "Check ParentName casing and load order."));
            }

            foreach (var reference in GetUnresolvedReferences(def, serverData).Take(8))
            {
                findings.Add(new AuditFinding(
                    "warning",
                    "unresolved_reference",
                    $"Definition '{def.DefName}' references '{reference.Value}' but it was not found in loaded defs",
                    def.Mod.PackageId,
                    def.DefName,
                    def.FilePath,
                    null,
                    "Verify the referenced DefName or gate it behind the correct dependency."));
            }
        }

        foreach (var hotspot in BuildPatchHotspots(serverData, null))
        {
            var touchesScope = hotspot.Mods.Any(modId => scopedMods.Any(scopedMod => string.Equals(scopedMod.PackageId, modId, StringComparison.OrdinalIgnoreCase))) ||
                               hotspot.TargetDefs.Any(targetDef => scopedDefs.Any(def => string.Equals(def.DefName, targetDef, StringComparison.OrdinalIgnoreCase))) ||
                               scopedPatches.Any(patch => string.Equals(NormalizeXPath(patch.XPath), NormalizeXPath(hotspot.XPath), StringComparison.OrdinalIgnoreCase));

            if (!touchesScope)
            {
                continue;
            }

            findings.Add(new AuditFinding(
                hotspot.Severity,
                "patch_hotspot",
                $"Multiple mods target the same XPath '{hotspot.XPath}'",
                hotspot.Mods.FirstOrDefault(),
                hotspot.TargetDefs.FirstOrDefault(),
                hotspot.Files.FirstOrDefault(),
                hotspot.XPath,
                "Review load order and consider narrowing the XPath target."));
        }

        return findings
            .DistinctBy(finding => $"{finding.Code}|{finding.ModPackageId}|{finding.DefName}|{finding.FilePath}|{finding.XPath}|{finding.Message}", StringComparer.OrdinalIgnoreCase);
    }

    private static List<DlcDependencyFinding> AnalyzeDlcDependenciesForMod(ServerData serverData, ModInfo mod, HashSet<string> allowedSet)
    {
        var findings = new List<DlcDependencyFinding>();

        foreach (var dependency in mod.Dependencies.Concat(mod.LoadBefore).Concat(mod.LoadAfter).Concat(mod.IncompatibleWith))
        {
            if (TryNormalizeOfficialContent(dependency, out var canonical) && !allowedSet.Contains(canonical))
            {
                findings.Add(new DlcDependencyFinding(
                    "error",
                    "metadata_reference",
                    canonical,
                    $"Mod metadata references forbidden official content '{dependency}'",
                    mod.PackageId,
                    null,
                    Path.Combine(mod.Path, "About", "About.xml"),
                    null,
                    dependency));
            }
        }

        foreach (var def in GetDefsForMod(serverData, mod))
        {
            foreach (var reference in GetResolvedReferences(def, serverData))
            {
                if (TryGetOfficialContentOwner(reference.TargetMod, out var canonical) && !allowedSet.Contains(canonical))
                {
                    findings.Add(new DlcDependencyFinding(
                        "error",
                        "def_reference",
                        canonical,
                        $"Definition '{def.DefName}' references '{reference.ToDef}' from {canonical}",
                        mod.PackageId,
                        def.DefName,
                        def.FilePath,
                        null,
                        reference.ToDef));
                }
            }
        }

        foreach (var patch in serverData.GlobalPatches.Where(patch => string.Equals(patch.Mod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var conditionalRef in patch.Conditions?.ModLoaded ?? [])
            {
                if (TryNormalizeOfficialContent(conditionalRef, out var canonical) && !allowedSet.Contains(canonical))
                {
                    findings.Add(new DlcDependencyFinding(
                        "error",
                        "patch_condition",
                        canonical,
                        $"Patch conditions require forbidden official content '{conditionalRef}'",
                        mod.PackageId,
                        null,
                        patch.FilePath,
                        patch.XPath,
                        conditionalRef));
                }
            }

            foreach (var targetDefName in ExtractDefNamesFromXPath(patch.XPath))
            {
                var referencedDef = FindDefByName(serverData, targetDefName);
                if (referencedDef != null &&
                    TryGetOfficialContentOwner(referencedDef.Mod, out var canonical) &&
                    !allowedSet.Contains(canonical))
                {
                    findings.Add(new DlcDependencyFinding(
                        "error",
                        "patch_target",
                        canonical,
                        $"Patch targets '{targetDefName}' from {canonical}",
                        mod.PackageId,
                        targetDefName,
                        patch.FilePath,
                        patch.XPath,
                        targetDefName));
                }
            }
        }

        return findings
            .DistinctBy(finding => $"{finding.Kind}|{finding.ForbiddenContent}|{finding.DefName}|{finding.FilePath}|{finding.XPath}|{finding.Reference}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<LogIncident> AnalyzePlayerLog(ServerData serverData, string logPath, ModInfo? targetMod)
    {
        var lines = File.ReadAllLines(logPath);
        var incidents = new List<LogIncident>();

        for (var index = 0; index < lines.Length; index++)
        {
            var rawLine = lines[index];
            var trimmedLine = rawLine.Trim();
            if (!IsInterestingLogLine(trimmedLine))
            {
                continue;
            }

            var context = new List<string> { trimmedLine };
            var cursor = index + 1;
            var additionalLines = 0;
            while (cursor < lines.Length && additionalLines < 6)
            {
                var candidate = lines[cursor].Trim();
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    break;
                }

                if (additionalLines > 0 && IsInterestingLogLine(candidate))
                {
                    break;
                }

                context.Add(candidate);
                cursor++;
                additionalLines++;
            }

            var text = string.Join(" | ", context);
            var mentionedMods = FindMentionedMods(serverData, text).ToList();
            if (targetMod != null &&
                !mentionedMods.Contains(targetMod.PackageId, StringComparer.OrdinalIgnoreCase) &&
                !mentionedMods.Contains(targetMod.Name, StringComparer.OrdinalIgnoreCase) &&
                !text.Contains(targetMod.PackageId, StringComparison.OrdinalIgnoreCase) &&
                !text.Contains(targetMod.Name, StringComparison.OrdinalIgnoreCase))
            {
                index = cursor - 1;
                continue;
            }

            incidents.Add(new LogIncident(
                CategorizeLogLine(trimmedLine),
                NormalizeLogSignature(trimmedLine),
                trimmedLine,
                text,
                context,
                ExtractMentionedDefNames(serverData, text).ToList(),
                FilePathRegex.Matches(text).Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
                mentionedMods));

            index = cursor - 1;
        }

        return incidents;
    }

    private static PatchHotspot[] BuildPatchHotspots(ServerData serverData, string? modPackageId)
    {
        var relevantPatches = serverData.GlobalPatches
            .Where(patch => !string.IsNullOrWhiteSpace(patch.XPath))
            .ToList();

        return relevantPatches
            .GroupBy(patch => NormalizeXPath(patch.XPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(patch => patch.Mod.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            .Where(group => string.IsNullOrWhiteSpace(modPackageId) ||
                            group.Any(patch =>
                                string.Equals(patch.Mod.PackageId, modPackageId, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(patch.Mod.Name, modPackageId, StringComparison.OrdinalIgnoreCase)))
            .Select(group =>
            {
                var patches = group.ToList();
                var operations = patches.Select(patch => patch.Operation.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
                var severity = DeterminePatchHotspotSeverity(patches);
                var targetDefs = patches
                    .SelectMany(patch => ExtractDefNamesFromXPath(patch.XPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList();

                return new PatchHotspot(
                    severity,
                    group.Key,
                    patches.Count,
                    operations,
                    patches.Select(patch => patch.Mod.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
                    patches.Select(patch => patch.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
                    targetDefs);
            })
            .ToArray();
    }

    private static PatchHotspot[] BuildAllPatchHotspots(ServerData serverData, string? modPackageId)
    {
        var relevantPatches = serverData.GlobalPatches
            .Where(patch => !string.IsNullOrWhiteSpace(patch.XPath))
            .ToList();

        return relevantPatches
            .GroupBy(patch => NormalizeXPath(patch.XPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Where(group => string.IsNullOrWhiteSpace(modPackageId) ||
                            group.Any(patch =>
                                string.Equals(patch.Mod.PackageId, modPackageId, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(patch.Mod.Name, modPackageId, StringComparison.OrdinalIgnoreCase)))
            .Select(group =>
            {
                var patches = group.ToList();
                var distinctMods = patches
                    .Select(patch => patch.Mod.PackageId)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var severity = distinctMods.Count > 1 ? DeterminePatchHotspotSeverity(patches) : "info";
                var operations = patches
                    .Select(patch => patch.Operation.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var targetDefs = patches
                    .SelectMany(patch => ExtractDefNamesFromXPath(patch.XPath))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(8)
                    .ToList();

                return new PatchHotspot(
                    severity,
                    group.Key,
                    patches.Count,
                    operations,
                    distinctMods,
                    patches.Select(patch => patch.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList(),
                    targetDefs);
            })
            .ToArray();
    }

    private static Dictionary<string, LogGroup> GroupLogIncidents(IEnumerable<LogIncident> incidents)
    {
        return incidents
            .GroupBy(incident => incident.Signature, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var sample = group.First();
                    return new LogGroup(
                        sample.Category,
                        sample.Signature,
                        sample.Headline,
                        group.Count(),
                        group.SelectMany(item => item.MentionedDefs).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList(),
                        group.SelectMany(item => item.MentionedFiles).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList(),
                        group.SelectMany(item => item.MentionedMods).Distinct(StringComparer.OrdinalIgnoreCase).Take(6).ToList());
                },
                StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> ResolveChangedFiles(ProjectContext projectContext, string? baseRef, string[]? explicitPaths)
    {
        if (explicitPaths != null && explicitPaths.Length > 0)
        {
            return explicitPaths
                .SelectMany(path => ExpandChangedPath(path, projectContext.ProjectRoot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        if (!Directory.Exists(projectContext.ProjectRoot))
        {
            return [];
        }

        return string.IsNullOrWhiteSpace(baseRef)
            ? GetChangedFilesFromGitStatus(projectContext.ProjectRoot)
            : GetChangedFilesFromGitDiff(projectContext.ProjectRoot, baseRef);
    }

    private static List<string> GetChangedFilesFromGitStatus(string projectRoot)
    {
        var output = RunGit(projectRoot, "status", "--porcelain", "--untracked-files=all");
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Select(line => line.Length >= 4 ? line[3..].Trim() : string.Empty)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Contains(" -> ", StringComparison.Ordinal) ? path.Split(" -> ", StringSplitOptions.TrimEntries).Last() : path)
            .Select(path => Path.GetFullPath(Path.Combine(projectRoot, path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> GetChangedFilesFromGitDiff(string projectRoot, string baseRef)
    {
        var output = RunGit(projectRoot, "diff", "--name-only", "--diff-filter=ACMR", $"{baseRef}...HEAD");
        return output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(path => Path.GetFullPath(Path.Combine(projectRoot, path.Trim())))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string RunGit(string projectRoot, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = projectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr)
                ? "git command failed while resolving changed files."
                : stderr.Trim());
        }

        return stdout;
    }

    private static IEnumerable<string> ExpandChangedPath(string path, string projectRoot)
    {
        var resolved = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));

        if (Directory.Exists(resolved))
        {
            return Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories);
        }

        return [resolved];
    }

    private static IEnumerable<RimWorldDef> GetChangedDefs(ServerData serverData, IReadOnlyCollection<string> changedFiles)
    {
        var changedSet = changedFiles.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return serverData.Defs.Values
            .Where(def => changedSet.Contains(NormalizePath(ResolveDefFilePath(def))));
    }

    private static IEnumerable<PatchOperation> GetChangedPatches(ServerData serverData, IReadOnlyCollection<string> changedFiles)
    {
        var changedSet = changedFiles.Select(NormalizePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return serverData.GlobalPatches
            .Where(patch => changedSet.Contains(NormalizePath(patch.FilePath)));
    }

    private static IEnumerable<ModInfo> GetChangedMods(ServerData serverData, IReadOnlyCollection<string> changedFiles)
    {
        return serverData.Mods.Values
            .Where(mod => changedFiles.Any(path => IsPathUnderRoot(path, mod.Path)));
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimEnd(Path.DirectorySeparatorChar);

    private static bool IsPathUnderRoot(string path, string root)
    {
        var normalizedPath = NormalizePath(path);
        var normalizedRoot = NormalizePath(root);
        return normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyChangedPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".xml" => "xml",
            ".cs" => "code",
            ".png" or ".jpg" or ".jpeg" or ".dds" => "texture",
            ".wav" or ".ogg" => "sound",
            ".dll" => "assembly",
            _ => "other"
        };
    }

    private static IEnumerable<RimWorldDef> FindSimilarDefCandidates(ServerData serverData, string reference)
    {
        var normalizedReference = reference.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);
        var allDefs = serverData.Defs.Values.Concat(serverData.AbstractDefs.Values)
            .DistinctBy(def => def.DefName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var closeMatches = allDefs
            .Where(def =>
                def.DefName.Contains(reference, StringComparison.OrdinalIgnoreCase) ||
                reference.Contains(def.DefName, StringComparison.OrdinalIgnoreCase) ||
                def.DefName.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal)
                    .Contains(normalizedReference, StringComparison.OrdinalIgnoreCase))
            .Take(12)
            .ToList();

        if (closeMatches.Count >= 6)
        {
            return closeMatches;
        }

        return allDefs
            .OrderBy(def => ComputeEditDistance(def.DefName, reference))
            .ThenBy(def => def.DefName, StringComparer.OrdinalIgnoreCase)
            .Take(8);
    }

    private static int ComputeEditDistance(string left, string right)
    {
        var matrix = new int[left.Length + 1, right.Length + 1];

        for (var i = 0; i <= left.Length; i++)
        {
            matrix[i, 0] = i;
        }

        for (var j = 0; j <= right.Length; j++)
        {
            matrix[0, j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = char.ToUpperInvariant(left[i - 1]) == char.ToUpperInvariant(right[j - 1]) ? 0 : 1;
                matrix[i, j] = Math.Min(
                    Math.Min(matrix[i - 1, j] + 1, matrix[i, j - 1] + 1),
                    matrix[i - 1, j - 1] + cost);
            }
        }

        return matrix[left.Length, right.Length];
    }

    private static bool WouldFlipRelativeOrder(double currentOrder, double proposedOrder, int otherLoadOrder)
    {
        var currentlyBefore = currentOrder < otherLoadOrder;
        var proposedBefore = proposedOrder < otherLoadOrder;
        return currentlyBefore != proposedBefore;
    }

    private static IEnumerable<RimWorldDef> GetCoverageDefs(ServerData serverData, string? modPackageId, string? defType)
    {
        IEnumerable<RimWorldDef> defs = string.IsNullOrWhiteSpace(modPackageId)
            ? serverData.Defs.Values.Where(def => !def.Mod.IsCore && !def.Mod.IsDLC)
            : GetDefsForMod(serverData, ResolveMod(serverData, modPackageId) ?? new ModInfo { PackageId = "__missing__" });

        if (!string.IsNullOrWhiteSpace(defType))
        {
            defs = defs.Where(def => string.Equals(def.Type, defType, StringComparison.OrdinalIgnoreCase));
        }

        return defs;
    }

    private static IEnumerable<ModInfo> GetTargetMods(ServerData serverData, string? modPackageId)
    {
        if (!string.IsNullOrWhiteSpace(modPackageId))
        {
            var mod = ResolveMod(serverData, modPackageId);
            return mod == null ? [] : [mod];
        }

        return serverData.Mods.Values
            .Where(mod => !mod.IsCore && !mod.IsDLC)
            .OrderBy(mod => mod.LoadOrder);
    }

    private static IEnumerable<RimWorldDef> GetDefsForMod(ServerData serverData, ModInfo mod) =>
        serverData.Defs.Values.Where(def => string.Equals(def.Mod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase));

    private static ModInfo? ResolveMod(ServerData serverData, string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        if (serverData.Mods.TryGetValue(identifier, out var directMatch))
        {
            return directMatch;
        }

        return serverData.Mods.Values.FirstOrDefault(mod =>
            string.Equals(mod.PackageId, identifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mod.Name, identifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(mod.Path), identifier, StringComparison.OrdinalIgnoreCase));
    }

    private static RimWorldDef? FindDefByName(ServerData serverData, string? defName)
    {
        if (string.IsNullOrWhiteSpace(defName))
        {
            return null;
        }

        if (serverData.Defs.TryGetValue(defName, out var directMatch))
        {
            return directMatch;
        }

        if (serverData.AbstractDefs.TryGetValue(defName, out var abstractMatch))
        {
            return abstractMatch;
        }

        return serverData.Defs.Values.FirstOrDefault(def => string.Equals(def.DefName, defName, StringComparison.OrdinalIgnoreCase))
               ?? serverData.AbstractDefs.Values.FirstOrDefault(def => string.Equals(def.DefName, defName, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<ResolvedReference> GetResolvedReferences(RimWorldDef def, ServerData serverData)
    {
        return EnumerateSymbolEntries(def.Content)
            .Where(entry => !string.Equals(entry.FieldName, "defName", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !IsRuntimeClassPath(entry.Path))
            .Select(entry =>
            {
                var referencedDef = FindDefByName(serverData, entry.Value);
                return referencedDef == null
                    ? null
                    : new ResolvedReference(def.DefName, referencedDef.DefName, entry.Path, referencedDef.Type, referencedDef.Mod);
            })
            .Where(reference => reference != null)
            .Select(reference => reference!)
            .DistinctBy(reference => $"{reference.FromDef}|{reference.ToDef}|{reference.Path}", StringComparer.OrdinalIgnoreCase);
    }

    private static List<SymbolEntry> GetUnresolvedReferences(RimWorldDef def, ServerData serverData)
    {
        return EnumerateSymbolEntries(def.Content)
            .Where(entry => !string.Equals(entry.FieldName, "defName", StringComparison.OrdinalIgnoreCase))
            .Where(entry => !IsRuntimeClassPath(entry.Path))
            .Where(entry => IsLikelyReferencePath(entry.Path))
            .Where(entry => !string.Equals(entry.Value, def.DefName, StringComparison.OrdinalIgnoreCase))
            .Where(entry => !string.Equals(entry.Value, def.Parent, StringComparison.OrdinalIgnoreCase))
            .Where(entry => FindDefByName(serverData, entry.Value) == null)
            .DistinctBy(entry => $"{entry.Path}|{entry.Value}", StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<SymbolEntry> EnumerateSymbolEntries(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            var path = GetElementPath(element);
            var fieldName = element.Name.LocalName;

            if (!element.HasElements)
            {
                var value = element.Value.Trim();
                if (LooksLikeSymbol(value) && !IgnoredLeafFields.Contains(fieldName))
                {
                    yield return new SymbolEntry(value, path, fieldName);
                }
            }

            foreach (var attribute in element.Attributes())
            {
                var value = attribute.Value.Trim();
                if (LooksLikeSymbol(value))
                {
                    yield return new SymbolEntry(value, $"{path}/@{attribute.Name.LocalName}", attribute.Name.LocalName);
                }
            }
        }
    }

    private static string GetElementPath(XElement element) =>
        string.Join("/", element.AncestorsAndSelf().Reverse().Select(node => node.Name.LocalName));

    private static bool LooksLikeSymbol(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length < 2 || value.Length > 120)
        {
            return false;
        }

        if (value.Contains(' ') || value.Contains('\t') || value.Contains('\n') || value.Contains('\r'))
        {
            return false;
        }

        if (value.Contains('/') || value.Contains('\\') || value.StartsWith("<", StringComparison.Ordinal))
        {
            return false;
        }

        if (bool.TryParse(value, out _) || decimal.TryParse(value, out _))
        {
            return false;
        }

        return value.All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '-');
    }

    private static bool IsRuntimeClassPath(string path)
    {
        var leaf = path.Split('/').Last().TrimStart('@');
        if (RuntimeClassFields.Contains(leaf))
        {
            return true;
        }

        return leaf.EndsWith("Class", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("/comps/li/@Class", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyReferencePath(string path)
    {
        var normalized = path.ToLowerInvariant();
        if (normalized.EndsWith("/defname"))
        {
            return false;
        }

        return normalized.Contains("def") ||
               normalized.Contains("thing") ||
               normalized.Contains("recipe") ||
               normalized.Contains("research") ||
               normalized.Contains("hediff") ||
               normalized.Contains("trait") ||
               normalized.Contains("thought") ||
               normalized.Contains("race") ||
               normalized.Contains("stuff") ||
               normalized.Contains("biome") ||
               normalized.Contains("terrain") ||
               normalized.Contains("plant") ||
               normalized.Contains("faction") ||
               normalized.Contains("job") ||
               normalized.Contains("workgiver") ||
               normalized.Contains("pawn") ||
               normalized.Contains("ability") ||
               normalized.Contains("gene") ||
               normalized.Contains("designation") ||
               normalized.Contains("category");
    }

    private static string? NormalizeScopeType(string scopeType) =>
        scopeType.Trim().ToLowerInvariant() switch
        {
            "mod" => "mod",
            "def" => "def",
            "def_type" => "def_type",
            "type" => "def_type",
            "path" => "path",
            _ => null
        };

    private static IEnumerable<ModInfo> GetScopedMods(ServerData serverData, string scopeType, string scopeValue)
    {
        return scopeType switch
        {
            "mod" => ResolveMod(serverData, scopeValue) is { } mod ? [mod] : [],
            "def" => FindDefByName(serverData, scopeValue) is { } def ? [def.Mod] : [],
            "path" => serverData.Mods.Values.Where(mod => mod.Path.Contains(scopeValue, StringComparison.OrdinalIgnoreCase)),
            _ => []
        };
    }

    private static IEnumerable<RimWorldDef> GetScopedDefs(ServerData serverData, string scopeType, string scopeValue)
    {
        return scopeType switch
        {
            "mod" => ResolveMod(serverData, scopeValue) is { } mod ? GetDefsForMod(serverData, mod) : [],
            "def" => FindDefByName(serverData, scopeValue) is { } def ? [def] : [],
            "def_type" => serverData.Defs.Values.Where(def => string.Equals(def.Type, scopeValue, StringComparison.OrdinalIgnoreCase)),
            "path" => serverData.Defs.Values.Where(def =>
                ResolveDefFilePath(def).Contains(scopeValue, StringComparison.OrdinalIgnoreCase) ||
                def.Mod.Path.Contains(scopeValue, StringComparison.OrdinalIgnoreCase)),
            _ => []
        };
    }

    private static string ResolveDefFilePath(RimWorldDef def) =>
        Path.IsPathRooted(def.FilePath)
            ? def.FilePath
            : Path.Combine(def.Mod.Path, def.FilePath);

    private static IEnumerable<PatchOperation> GetScopedPatches(ServerData serverData, string scopeType, string scopeValue, IReadOnlyCollection<RimWorldDef> scopedDefs)
    {
        return scopeType switch
        {
            "mod" => ResolveMod(serverData, scopeValue) is { } mod
                ? serverData.GlobalPatches.Where(patch => string.Equals(patch.Mod.PackageId, mod.PackageId, StringComparison.OrdinalIgnoreCase))
                : [],
            "def" => serverData.GlobalPatches.Where(patch =>
                patch.XPath.Contains(scopeValue, StringComparison.OrdinalIgnoreCase) ||
                ExtractDefNamesFromXPath(patch.XPath).Contains(scopeValue, StringComparer.OrdinalIgnoreCase)),
            "path" => serverData.GlobalPatches.Where(patch => patch.FilePath.Contains(scopeValue, StringComparison.OrdinalIgnoreCase)),
            "def_type" => serverData.GlobalPatches.Where(patch =>
                ExtractDefNamesFromXPath(patch.XPath).Any(targetDef => scopedDefs.Any(def => string.Equals(def.DefName, targetDef, StringComparison.OrdinalIgnoreCase)))),
            _ => []
        };
    }

    private static IReadOnlyCollection<string> ExtractDefNamesFromXPath(string? xpath)
    {
        if (string.IsNullOrWhiteSpace(xpath))
        {
            return Array.Empty<string>();
        }

        return DefNameXPathRegex.Matches(xpath)
            .Select(match => match.Groups[1].Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeXPath(string? xpath) =>
        string.IsNullOrWhiteSpace(xpath)
            ? string.Empty
            : xpath.Replace(" ", string.Empty)
                .Replace("\t", string.Empty)
                .Replace("\r", string.Empty)
                .Replace("\n", string.Empty)
                .Trim();

    private static string DeterminePatchHotspotSeverity(IReadOnlyCollection<PatchOperation> patches)
    {
        var operations = patches.Select(patch => patch.Operation).Distinct().ToList();
        if (operations.Contains(PatchOperationType.PatchOperationRemove) &&
            operations.Any(operation => operation != PatchOperationType.PatchOperationRemove))
        {
            return "error";
        }

        if (operations.All(operation => operation == PatchOperationType.PatchOperationRemove))
        {
            return "error";
        }

        return patches.Count >= 3 ? "warning" : "info";
    }

    private static bool TryNormalizeOfficialContent(string rawValue, out string canonical)
    {
        foreach (var entry in OfficialContentAliases)
        {
            if (string.Equals(entry.Key, rawValue, StringComparison.OrdinalIgnoreCase) ||
                entry.Value.Any(alias => string.Equals(alias, rawValue, StringComparison.OrdinalIgnoreCase)))
            {
                canonical = entry.Key;
                return true;
            }
        }

        canonical = string.Empty;
        return false;
    }

    private static bool TryGetOfficialContentOwner(ModInfo mod, out string canonical)
    {
        if (mod.IsCore)
        {
            canonical = "Core";
            return true;
        }

        return TryNormalizeOfficialContent(mod.PackageId, out canonical) ||
               TryNormalizeOfficialContent(mod.Name, out canonical) ||
               TryNormalizeOfficialContent(Path.GetFileName(mod.Path), out canonical);
    }

    private static HashSet<string> ParseAllowedContentSet(string allowedDlcs)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Core" };
        foreach (var token in allowedDlcs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryNormalizeOfficialContent(token, out var canonical))
            {
                allowed.Add(canonical);
            }
        }

        return allowed;
    }

    private static string ToSeverity(ConflictSeverity severity) => severity switch
    {
        ConflictSeverity.Error => "error",
        ConflictSeverity.Warning => "warning",
        _ => "info"
    };

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "error" => 3,
        "warning" => 2,
        "info" => 1,
        _ => 0
    };

    private static bool MeetsSeverityThreshold(string severity, string minimumSeverity) =>
        SeverityRank(severity) >= SeverityRank(minimumSeverity);

    private static bool IsInterestingLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        return line.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("XML error", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Config error", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Could not resolve cross-reference", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Could not load reference", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Failed to find", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Error while", StringComparison.OrdinalIgnoreCase) ||
               line.Contains("Harmony", StringComparison.OrdinalIgnoreCase);
    }

    private static string CategorizeLogLine(string line)
    {
        if (line.Contains("XML error", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Config error", StringComparison.OrdinalIgnoreCase))
        {
            return "xml";
        }

        if (line.Contains("cross-reference", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Could not load reference", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Failed to find", StringComparison.OrdinalIgnoreCase))
        {
            return "reference";
        }

        if (line.Contains("Harmony", StringComparison.OrdinalIgnoreCase))
        {
            return "patch";
        }

        return "exception";
    }

    private static string NormalizeLogSignature(string line)
    {
        var normalized = FilePathRegex.Replace(line, "<file>");
        normalized = QuotedValueRegex.Replace(normalized, "\"<value>\"");
        normalized = NumberRegex.Replace(normalized, "#");
        normalized = AngleBracketRegex.Replace(normalized, "<xml>");
        return normalized.Trim();
    }

    private static IEnumerable<string> ExtractMentionedDefNames(ServerData serverData, string text)
    {
        var quotedValues = QuotedValueRegex.Matches(text).Select(match => match.Groups[1].Value);
        return quotedValues
            .Select(value => FindDefByName(serverData, value)?.DefName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)!;
    }

    private static IEnumerable<string> FindMentionedMods(ServerData serverData, string text)
    {
        return serverData.Mods.Values
            .Where(mod => text.Contains(mod.PackageId, StringComparison.OrdinalIgnoreCase) ||
                          text.Contains(mod.Name, StringComparison.OrdinalIgnoreCase))
            .SelectMany(mod => new[] { mod.PackageId, mod.Name })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8);
    }
}
