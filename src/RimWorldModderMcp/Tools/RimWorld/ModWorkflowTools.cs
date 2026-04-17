using System.ComponentModel;
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

    [McpServerTool, Description("Group RimWorld Player.log problems into compact issue buckets for fast debugging.")]
    public static string TriagePlayerLog(
        ServerData serverData,
        [Description("Absolute path to the RimWorld Player.log or Player-prev.log file")] string logPath,
        [Description("Optional: only keep incidents clearly tied to this mod package ID")] string? modPackageId = null,
        [Description("Maximum grouped issue buckets to return")] int maxGroups = 15)
    {
        if (!File.Exists(logPath))
        {
            return Serialize(new { error = $"Log file '{logPath}' was not found" });
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

        var incidents = AnalyzePlayerLog(serverData, logPath, targetMod);
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
            logPath,
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

    [McpServerTool, Description("Validate a loaded def against parent, reference, and class-like runtime signals without in-process decompilation.")]
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

    [McpServerTool, Description("Scan loaded mods for references to DLC content outside an allowed compatibility target.")]
    public static string ScanDlcDependencies(
        ServerData serverData,
        [Description("Comma-separated DLC set to allow, for example 'Core,Biotech'")] string allowedDlcs = "Core,Biotech",
        [Description("Optional: specific mod package ID to scan")] string? modPackageId = null,
        [Description("Maximum findings to include per mod")] int maxFindingsPerMod = 12)
    {
        var allowedSet = ParseAllowedContentSet(allowedDlcs);
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

    [McpServerTool, Description("Produce a compact, token-aware audit of a mod, def, def type, or path scope.")]
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

    [McpServerTool, Description("Group XPath collisions and patch hotspots into compact conflict clusters.")]
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

    [McpServerTool, Description("Report compact coverage signals for loaded mod content, including references, patches, overrides, and broken refs.")]
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

    [McpServerTool, Description("Run a compact readiness check for one mod or all loaded custom mods.")]
    public static string ModReadyCheck(
        ServerData serverData,
        [Description("Optional: specific mod package ID to evaluate")] string? modPackageId = null,
        [Description("Comma-separated DLC compatibility target, for example 'Core,Biotech'")] string allowedDlcs = "Core,Biotech",
        [Description("Optional: Player.log path for runtime issue inclusion")] string? logPath = null,
        [Description("Maximum issue examples per check")] int maxIssues = 8)
    {
        var allowedSet = ParseAllowedContentSet(allowedDlcs);
        var targetMods = GetTargetMods(serverData, modPackageId).ToList();
        if (targetMods.Count == 0)
        {
            return Serialize(new { error = "No matching non-core, non-DLC mods were found" });
        }

        List<LogIncident> logIncidents = [];
        if (!string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath))
        {
            logIncidents = AnalyzePlayerLog(serverData, logPath, null);
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
                        status = modLogGroups.Count > 0 ? "warning" : string.IsNullOrWhiteSpace(logPath) ? "skipped" : "ready",
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
            logPath = !string.IsNullOrWhiteSpace(logPath) && File.Exists(logPath) ? logPath : null,
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
            .Where(patch => string.IsNullOrWhiteSpace(modPackageId) ||
                            string.Equals(patch.Mod.PackageId, modPackageId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(patch.Mod.Name, modPackageId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return relevantPatches
            .GroupBy(patch => NormalizeXPath(patch.XPath), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(patch => patch.Mod.PackageId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
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
                def.FilePath.Contains(scopeValue, StringComparison.OrdinalIgnoreCase) ||
                def.Mod.Path.Contains(scopeValue, StringComparison.OrdinalIgnoreCase)),
            _ => []
        };
    }

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
