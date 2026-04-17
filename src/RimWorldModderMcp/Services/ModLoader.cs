using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public class ModLoader
{
    private readonly string _rimworldPath;
    private readonly int _modBatchSize;
    private readonly List<string> _modDirs;
    private readonly ILogger<ModLoader> _logger;

    public ModLoader(string rimworldPath, int modBatchSize = 10, List<string>? modDirs = null, ILogger<ModLoader>? logger = null)
    {
        _rimworldPath = rimworldPath;
        _modBatchSize = modBatchSize;
        _modDirs = modDirs ?? [];
        _logger = logger ?? new NullLogger<ModLoader>();
    }

    public async Task LoadModsAsync(ServerData data)
    {
        var totalSw = Stopwatch.StartNew();
        
        _logger.LogInformation("Loading Core...");
        var coreSw = Stopwatch.StartNew();
        var corePath = Path.Combine(_rimworldPath, "Data", "Core");
        var coreInfo = await LoadModInfoAsync(corePath, 0, true, false);
        if (coreInfo != null)
        {
            data.Mods["Core"] = coreInfo;
            data.LoadOrder.Add("Core");
        }
        coreSw.Stop();
        _logger.LogInformation("   ⏱️  Core loaded in: {Duration:F2}s", coreSw.Elapsed.TotalSeconds);

        _logger.LogInformation("Loading DLCs...");
        var dlcSw = Stopwatch.StartNew();
        string[] dlcNames = ["Royalty", "Ideology", "Biotech", "Anomaly"];

        // Load DLCs in parallel
        List<Task<(string dlcName, ModInfo? modInfo)>> dlcTasks = [];
        var loadOrderIndex = 1;
        
        for (var i = 0; i < dlcNames.Length; i++)
        {
            var dlcName = dlcNames[i];
            var dlcPath = Path.Combine(_rimworldPath, "Data", dlcName);
            var currentLoadOrder = loadOrderIndex + i;
            
            if (Directory.Exists(dlcPath))
            {
                dlcTasks.Add(LoadDlcAsync(dlcPath, dlcName, currentLoadOrder));
            }
        }

        var dlcResults = await Task.WhenAll(dlcTasks);
        dlcSw.Stop();
        
        // Add DLCs to data in load order
        var dlcsLoaded = 0;
        foreach ((var dlcName, var modInfo) in dlcResults.OrderBy(r => r.modInfo?.LoadOrder))
        {
            if (modInfo != null)
            {
                data.Mods[dlcName] = modInfo;
                data.LoadOrder.Add(dlcName);
                loadOrderIndex = Math.Max(loadOrderIndex, modInfo.LoadOrder + 1);
                _logger.LogInformation("  Loaded DLC: {DLC}", dlcName);
                dlcsLoaded++;
            }
        }
        _logger.LogInformation("   ⏱️  {DlcCount} DLCs loaded in parallel in: {Duration:F2}s", dlcsLoaded, dlcSw.Elapsed.TotalSeconds);

        if (_modDirs.Count > 0)
        {
            _logger.LogInformation("Loading additional mods...");
            var modsSw = Stopwatch.StartNew();
            await LoadAdditionalModsAsync(data, loadOrderIndex);
            modsSw.Stop();
            _logger.LogInformation("   ⏱️  Additional mods loaded in: {Duration:F2}s", modsSw.Elapsed.TotalSeconds);
        }
        else
        {
            _logger.LogInformation("No additional mod directories specified");
        }

        _logger.LogInformation("Validating load order...");
        var validationSw = Stopwatch.StartNew();
        ValidateLoadOrder(data);
        validationSw.Stop();
        totalSw.Stop();
        
        _logger.LogInformation("   ⏱️  Load order validation took: {Duration:F2}s", validationSw.Elapsed.TotalSeconds);
        _logger.LogInformation("   ⏱️  Total mod loading took: {Duration:F2}s", totalSw.Elapsed.TotalSeconds);
    }

    public async Task LoadModAsync(string modPath, ServerData data)
    {
        var modInfo = await LoadModInfoAsync(modPath, data.LoadOrder.Count, false, false);
        if (modInfo != null)
        {
            data.Mods[modInfo.PackageId] = modInfo;
            if (!data.LoadOrder.Contains(modInfo.PackageId))
            {
                data.LoadOrder.Add(modInfo.PackageId);
            }
            _logger.LogInformation("Reloaded mod: {ModName} ({PackageId})", modInfo.Name, modInfo.PackageId);
        }
    }

    private async Task<ModInfo?> LoadModInfoAsync(string modPath, int loadOrder, bool isCore, bool isDLC)
    {
        var aboutPath = Path.Combine(modPath, "About", "About.xml");

        try
        {
            if (!File.Exists(aboutPath))
            {
                if (isCore || isDLC)
                {
                    return new ModInfo
                    {
                        PackageId = $"local.{Path.GetFileName(modPath)}",
                        Name = Path.GetFileName(modPath),
                        LoadOrder = loadOrder,
                        Path = modPath,
                        IsCore = isCore,
                        IsDLC = isDLC
                    };
                }
                return null;
            }

            var aboutContent = await File.ReadAllTextAsync(aboutPath);
            var doc = XDocument.Parse(aboutContent);
            var modMeta = doc.Root;

            if (modMeta == null) return null;

            ModInfo modInfo = new()
            {
                PackageId = modMeta.Element("packageId")?.Value 
                    ?? modMeta.Element("name")?.Value?.ToLower().Replace(" ", "") 
                    ?? Path.GetFileName(modPath),
                Name = modMeta.Element("name")?.Value ?? Path.GetFileName(modPath),
                Author = modMeta.Element("author")?.Value,
                SupportedVersions = ParseListField(modMeta.Element("supportedVersions")),
                LoadOrder = loadOrder,
                Path = modPath,
                IsCore = isCore,
                IsDLC = isDLC,
                Dependencies = ParseDependencies(modMeta.Element("modDependencies")),
                IncompatibleWith = ParseListField(modMeta.Element("incompatibleWith")),
                LoadBefore = ParseListField(modMeta.Element("loadBefore")),
                LoadAfter = ParseListField(modMeta.Element("loadAfter"))
            };

            return modInfo;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to load About.xml for {ModPath}: {Message}", modPath, ex.Message);
            
            if (isCore || isDLC)
            {
                return new ModInfo
                {
                    PackageId = $"local.{Path.GetFileName(modPath)}",
                    Name = Path.GetFileName(modPath),
                    LoadOrder = loadOrder,
                    Path = modPath,
                    IsCore = isCore,
                    IsDLC = isDLC
                };
            }
            
            return null;
        }
    }

    private List<string> ParseListField(XElement? element)
    {
        if (element == null) return [];

        List<string> result = [];
        foreach (var li in element.Elements("li"))
        {
            if (!string.IsNullOrWhiteSpace(li.Value))
            {
                result.Add(li.Value);
            }
        }
        return result;
    }

    private List<string> ParseDependencies(XElement? element)
    {
        if (element == null) return [];

        List<string> result = [];
        foreach (var li in element.Elements("li"))
        {
            var packageId = li.Element("packageId")?.Value ?? li.Value;
            if (!string.IsNullOrWhiteSpace(packageId))
            {
                result.Add(packageId);
            }
        }
        return result;
    }

    private async Task LoadAdditionalModsAsync(ServerData data, int startIndex)
    {
        List<Task<ModInfo?>> loadTasks = [];
        var currentIndex = startIndex;

        List<(string path, string name)> modDirectoriesToScan = [];
        for (var i = 0; i < _modDirs.Count; i++)
        {
            modDirectoriesToScan.Add((_modDirs[i], $"Mod Directory {i + 1}"));
        }

        foreach ((var dirPath, var dirName) in modDirectoriesToScan)
        {
            if (Directory.Exists(dirPath))
            {
                _logger.LogInformation("  Scanning {DirName}: {DirPath}", dirName, dirPath);
                var modFolders = Directory.GetDirectories(dirPath);
                
                foreach (var modPath in modFolders)
                {
                    loadTasks.Add(LoadModInfoAsync(modPath, currentIndex++, false, false));
                }
            }
            else
            {
                _logger.LogInformation("  Skipping {DirName} (not found): {DirPath}", dirName, dirPath);
            }
        }

        _logger.LogInformation("  Processing {Count} mod directories...", loadTasks.Count);
        var results = await ProcessInBatchesAsync(loadTasks, _modBatchSize);
        
        var modsLoaded = 0;
        foreach (var modInfo in results)
        {
            if (modInfo != null && !data.Mods.ContainsKey(modInfo.PackageId))
            {
                data.Mods[modInfo.PackageId] = modInfo;
                data.LoadOrder.Add(modInfo.PackageId);
                _logger.LogInformation("  ✓ Loaded {ModName} from: {ModPath}", modInfo.Name, modInfo.Path);
                modsLoaded++;
            }
        }
        
        _logger.LogInformation("  ✓ Loaded {Count} additional mods", modsLoaded);
    }

    private async Task<List<T?>> ProcessInBatchesAsync<T>(List<Task<T?>> tasks, int batchSize)
    {
        List<T?> results = [];
        
        for (var i = 0; i < tasks.Count; i += batchSize)
        {
            var batch = tasks.Skip(i).Take(batchSize);
            var batchResults = await Task.WhenAll(batch);
            results.AddRange(batchResults);
        }
        
        return results;
    }

    private void ValidateLoadOrder(ServerData data)
    {
        List<DefConflict> issues = [];

        foreach (var kvp in data.Mods)
        {
            var mod = kvp.Value;
            
            foreach (var dep in mod.Dependencies)
            {
                if (!data.Mods.ContainsKey(dep))
                {
                    issues.Add(new DefConflict
                    {
                        Type = ConflictType.MissingDependency,
                        Severity = ConflictSeverity.Error,
                        Mods = [mod],
                        Description = $"{mod.Name} requires {dep} which is not loaded",
                        Resolution = $"Install and enable {dep}"
                    });
                }
                else
                {
                    var depMod = data.Mods[dep];
                    if (depMod.LoadOrder > mod.LoadOrder)
                    {
                        issues.Add(new DefConflict
                        {
                            Type = ConflictType.MissingDependency,
                            Severity = ConflictSeverity.Error,
                            Mods = [mod, depMod],
                            Description = $"{mod.Name} requires {depMod.Name} but loads before it",
                            Resolution = $"Reorder mods so {depMod.Name} loads before {mod.Name}"
                        });
                    }
                }
            }

            foreach (var incompatId in mod.IncompatibleWith)
            {
                if (data.Mods.ContainsKey(incompatId))
                {
                    var incompatMod = data.Mods[incompatId];
                    issues.Add(new DefConflict
                    {
                        Type = ConflictType.Override,
                        Severity = ConflictSeverity.Error,
                        Mods = [mod, incompatMod],
                        Description = $"{mod.Name} is incompatible with {incompatMod.Name}",
                        Resolution = "Disable one of these mods"
                    });
                }
            }

            foreach (var afterId in mod.LoadAfter)
            {
                if (data.Mods.ContainsKey(afterId))
                {
                    var afterMod = data.Mods[afterId];
                    if (afterMod.LoadOrder > mod.LoadOrder)
                    {
                        issues.Add(new DefConflict
                        {
                            Type = ConflictType.Override,
                            Severity = ConflictSeverity.Warning,
                            Mods = [mod, afterMod],
                            Description = $"{mod.Name} should load after {afterMod.Name} but doesn't",
                            Resolution = $"Consider reordering: {afterMod.Name} should come before {mod.Name}"
                        });
                    }
                }
            }

            foreach (var beforeId in mod.LoadBefore)
            {
                if (data.Mods.ContainsKey(beforeId))
                {
                    var beforeMod = data.Mods[beforeId];
                    if (beforeMod.LoadOrder < mod.LoadOrder)
                    {
                        issues.Add(new DefConflict
                        {
                            Type = ConflictType.Override,
                            Severity = ConflictSeverity.Warning,
                            Mods = [mod, beforeMod],
                            Description = $"{mod.Name} should load before {beforeMod.Name} but doesn't",
                            Resolution = $"Consider reordering: {mod.Name} should come before {beforeMod.Name}"
                        });
                    }
                }
            }
        }

        data.Conflicts.AddRange(issues);
    }

    private async Task<(string dlcName, ModInfo? modInfo)> LoadDlcAsync(string dlcPath, string dlcName, int loadOrder)
    {
        var dlcInfo = await LoadModInfoAsync(dlcPath, loadOrder, false, true);
        return (dlcName, dlcInfo);
    }
}