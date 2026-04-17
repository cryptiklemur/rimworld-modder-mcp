using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public class ConflictDetector
{
    private readonly ILogger<ConflictDetector> _logger;

    public ConflictDetector(ILogger<ConflictDetector>? logger = null)
    {
        _logger = logger ?? new NullLogger<ConflictDetector>();
    }

    public void DetectAllConflicts(ServerData serverData)
    {
        _logger.LogInformation("🔍 Starting conflict detection...");
        var sw = Stopwatch.StartNew();

        serverData.Conflicts.Clear();

        // Detect different types of conflicts
        DetectDefOverrides(serverData);
        DetectPatchConflicts(serverData);
        DetectMissingDependencies(serverData);
        DetectCircularDependencies(serverData);

        sw.Stop();
        _logger.LogInformation("   ✓ Detected {ConflictCount} conflicts in {Duration:F2}s", 
            serverData.Conflicts.Count, sw.Elapsed.TotalSeconds);
    }

    private void DetectDefOverrides(ServerData serverData)
    {
        _logger.LogDebug("Detecting def overrides...");
        
        // Group defs by name to find duplicates
        Dictionary<string, List<RimWorldDef>> defsByName = [];
        
        foreach (var def in serverData.Defs.Values)
        {
            if (!defsByName.ContainsKey(def.DefName))
            {
                defsByName[def.DefName] = [];
            }
            defsByName[def.DefName].Add(def);
        }

        // Find defs with same name from different mods
        foreach (var kvp in defsByName)
        {
            var defs = kvp.Value;
            if (defs.Count > 1)
            {
                // Check if they're from different mods
                var modIds = defs.Select(d => d.Mod.PackageId).ToHashSet();
                if (modIds.Count > 1)
                {
                    // Determine which def "wins" based on load order
                    var winningDef = defs.OrderByDescending(d => d.Mod.LoadOrder).First();
                    var overriddenDefs = defs.Where(d => d != winningDef).ToList();

                    foreach (var overriddenDef in overriddenDefs)
                    {
                        serverData.Conflicts.Add(new DefConflict
                        {
                            Type = ConflictType.Override,
                            Severity = ConflictSeverity.Warning,
                            DefName = kvp.Key,
                            Mods = [overriddenDef.Mod, winningDef.Mod],
                            Description = $"Def '{kvp.Key}' from {overriddenDef.Mod.Name} is overridden by {winningDef.Mod.Name}",
                            Resolution = $"Mod {winningDef.Mod.Name} (load order {winningDef.Mod.LoadOrder}) takes precedence over {overriddenDef.Mod.Name} (load order {overriddenDef.Mod.LoadOrder})"
                        });
                    }
                }
            }
        }
    }

    private void DetectPatchConflicts(ServerData serverData)
    {
        _logger.LogDebug("Detecting patch conflicts...");
        
        // Group patches by their XPath targets
        Dictionary<string, List<PatchOperation>> patchesByXPath = [];
        
        foreach (var patch in serverData.GlobalPatches)
        {
            if (string.IsNullOrEmpty(patch.XPath))
                continue;

            // Normalize XPath for comparison (remove whitespace, etc.)
            var normalizedXPath = NormalizeXPath(patch.XPath);
            
            if (!patchesByXPath.ContainsKey(normalizedXPath))
            {
                patchesByXPath[normalizedXPath] = [];
            }
            patchesByXPath[normalizedXPath].Add(patch);
        }

        // Find conflicting patches
        foreach (var kvp in patchesByXPath)
        {
            var patches = kvp.Value;
            if (patches.Count > 1)
            {
                // Check for different mods targeting the same XPath
                var modIds = patches.Select(p => p.Mod.PackageId).ToHashSet();
                if (modIds.Count > 1)
                {
                    // Determine conflict severity based on operation types
                    var severity = DetermineXPathConflictSeverity(patches);
                    
                    serverData.Conflicts.Add(new DefConflict
                    {
                        Type = ConflictType.XPathConflict,
                        Severity = severity,
                        XPath = kvp.Key,
                        Mods = patches.Select(p => p.Mod).Distinct().ToList(),
                        Description = $"Multiple mods target the same XPath: {kvp.Key}",
                        Resolution = patches.Count == 2 ? 
                            $"Patches will apply in load order: {string.Join(" → ", patches.OrderBy(p => p.Mod.LoadOrder).Select(p => p.Mod.Name))}" :
                            $"{patches.Count} patches target this location - check load order"
                    });
                }
            }
        }
    }

    private void DetectMissingDependencies(ServerData serverData)
    {
        _logger.LogDebug("Detecting missing dependencies...");
        
        foreach (var mod in serverData.Mods.Values)
        {
            foreach (var dependencyId in mod.Dependencies)
            {
                if (!serverData.Mods.ContainsKey(dependencyId))
                {
                    serverData.Conflicts.Add(new DefConflict
                    {
                        Type = ConflictType.MissingDependency,
                        Severity = ConflictSeverity.Error,
                        Mods = [mod],
                        Description = $"Mod '{mod.Name}' requires missing dependency: {dependencyId}",
                        Resolution = $"Install mod with package ID: {dependencyId}"
                    });
                }
            }

            // Check load order for dependencies
            foreach (var dependencyId in mod.Dependencies)
            {
                if (serverData.Mods.TryGetValue(dependencyId, out var dependency))
                {
                    if (dependency.LoadOrder > mod.LoadOrder)
                    {
                        serverData.Conflicts.Add(new DefConflict
                        {
                            Type = ConflictType.MissingDependency,
                            Severity = ConflictSeverity.Warning,
                            Mods = [mod, dependency],
                            Description = $"Dependency '{dependency.Name}' loads after '{mod.Name}' (incorrect load order)",
                            Resolution = $"Move '{dependency.Name}' to load before '{mod.Name}' in your mod list"
                        });
                    }
                }
            }
        }
    }

    private void DetectCircularDependencies(ServerData serverData)
    {
        _logger.LogDebug("Detecting circular dependencies...");
        
        HashSet<string> visiting = [];
        HashSet<string> visited = [];
        
        foreach (var mod in serverData.Mods.Values)
        {
            if (!visited.Contains(mod.PackageId))
            {
                var cycle = DetectCycleDFS(mod.PackageId, serverData.Mods, visiting, visited, []);
                if (cycle != null)
                {
                    var cycleMods = cycle.Select(id => serverData.Mods[id]).ToList();
                    
                    serverData.Conflicts.Add(new DefConflict
                    {
                        Type = ConflictType.CircularDependency,
                        Severity = ConflictSeverity.Error,
                        Mods = cycleMods,
                        Description = $"Circular dependency detected: {string.Join(" → ", cycle)} → {cycle[0]}",
                        Resolution = "Remove one of the dependencies to break the cycle"
                    });
                }
            }
        }
    }

    private List<string>? DetectCycleDFS(string modId, Dictionary<string, ModInfo> mods, HashSet<string> visiting, HashSet<string> visited, List<string> path)
    {
        if (visiting.Contains(modId))
        {
            // Found a cycle - return the path from the cycle start
            var cycleStart = path.IndexOf(modId);
            return path.Skip(cycleStart).ToList();
        }

        if (visited.Contains(modId) || !mods.ContainsKey(modId))
        {
            return null;
        }

        visiting.Add(modId);
        path.Add(modId);

        foreach (var dependency in mods[modId].Dependencies)
        {
            var cycle = DetectCycleDFS(dependency, mods, visiting, visited, path);
            if (cycle != null)
            {
                return cycle;
            }
        }

        visiting.Remove(modId);
        visited.Add(modId);
        path.RemoveAt(path.Count - 1);
        
        return null;
    }

    private static string NormalizeXPath(string xpath)
    {
        // Basic XPath normalization for comparison
        return xpath.Trim()
                   .Replace(" ", "")
                   .Replace("\t", "")
                   .Replace("\n", "")
                   .Replace("\r", "");
    }

    private static ConflictSeverity DetermineXPathConflictSeverity(List<PatchOperation> patches)
    {
        // Check the types of operations
        var operations = patches.Select(p => p.Operation).ToHashSet();
        
        // Multiple Remove operations on same target = Error
        if (operations.Count == 1 && operations.Contains(PatchOperationType.PatchOperationRemove))
        {
            return ConflictSeverity.Error;
        }
        
        // Mix of Remove and Add/Replace = Warning
        if (operations.Contains(PatchOperationType.PatchOperationRemove) && 
            (operations.Contains(PatchOperationType.PatchOperationAdd) || 
             operations.Contains(PatchOperationType.PatchOperationReplace)))
        {
            return ConflictSeverity.Warning;
        }
        
        // Multiple Replace operations = Warning
        if (operations.Contains(PatchOperationType.PatchOperationReplace) && patches.Count > 1)
        {
            return ConflictSeverity.Warning;
        }
        
        // Multiple Add operations = Info (might be okay)
        return ConflictSeverity.Info;
    }

    public List<DefConflict> GetConflictsByMod(ServerData serverData, string modPackageId)
    {
        return serverData.Conflicts
            .Where(c => c.Mods.Any(m => m.PackageId.Equals(modPackageId, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public List<DefConflict> GetConflictsByType(ServerData serverData, ConflictType type)
    {
        return serverData.Conflicts.Where(c => c.Type == type).ToList();
    }

    public List<DefConflict> GetConflictsBySeverity(ServerData serverData, ConflictSeverity severity)
    {
        return serverData.Conflicts.Where(c => c.Severity == severity).ToList();
    }
}