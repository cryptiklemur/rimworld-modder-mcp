using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public class DefParser
{
    private readonly ILogger<DefParser> _logger;
    private readonly object _dataLock = new object();

    public DefParser(ILogger<DefParser>? logger = null)
    {
        _logger = logger ?? new NullLogger<DefParser>();
    }

    public async Task ScanModDefsAsync(ModInfo mod, ServerData data)
    {
        await ScanModDefsAsync(mod, data, "1.6"); // Default version for backward compatibility
    }

    public async Task ScanModDefsAsync(ModInfo mod, ServerData data, string rimworldVersion)
    {
        // Use LoadFolders.xml rules to choose which mod folders to scan for this version
        var loadFolders = ParseLoadFolders(mod.Path, rimworldVersion);
        
        foreach (var folder in loadFolders)
        {
            // Scan Defs folder
            var defsPath = Path.Combine(mod.Path, folder, "Defs");
            if (Directory.Exists(defsPath))
            {
                await ScanDirectoryAsync(defsPath, mod, data, isPatches: false);
            }
            
            // Scan Patches folder
            var patchesPath = Path.Combine(mod.Path, folder, "Patches");
            if (Directory.Exists(patchesPath))
            {
                await ScanDirectoryAsync(patchesPath, mod, data, isPatches: true);
            }
        }
    }

    private async Task ScanDirectoryAsync(string dir, ModInfo mod, ServerData data, bool isPatches = false)
    {
        try
        {
            List<string> directories = [];
            List<string> xmlFiles = [];

            foreach (var entry in Directory.GetFiles(dir, "*.xml"))
            {
                xmlFiles.Add(entry);
            }

            foreach (var entry in Directory.GetDirectories(dir))
            {
                directories.Add(entry);
            }

            var xmlTasks = xmlFiles.Select(filePath => 
                isPatches ? ProcessPatchFileAsync(filePath, mod, data) : ProcessXMLFileAsync(filePath, mod, data)
            ).ToList();
            await ProcessInBatchesAsync(xmlTasks, 5);

            var dirTasks = directories.Select(dirPath => ScanDirectoryAsync(dirPath, mod, data, isPatches)).ToList();
            await Task.WhenAll(dirTasks);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to scan directory {Directory}: {Message}", dir, ex.Message);
        }
    }

    private async Task ProcessXMLFileAsync(string filePath, ModInfo mod, ServerData data)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath);
            var doc = XDocument.Parse(content);

            if (doc.Root?.Name.LocalName == "Defs")
            {
                await ProcessDefsFileAsync(doc.Root, filePath, mod, data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to parse {FilePath}: {Message}", filePath, ex.Message);
        }
    }

    private async Task ProcessInBatchesAsync(List<Task> tasks, int batchSize)
    {
        for (var i = 0; i < tasks.Count; i += batchSize)
        {
            var batch = tasks.Skip(i).Take(batchSize);
            await Task.WhenAll(batch);
        }
    }

    private Task ProcessDefsFileAsync(XElement defsElement, string filePath, ModInfo mod, ServerData data)
    {
        foreach (var defElement in defsElement.Elements())
        {
            var defType = defElement.Name.LocalName;
            
            var defName = defElement.Element("defName")?.Value 
                          ?? defElement.Attribute("Name")?.Value;
            
            var isAbstract = defElement.Attribute("Abstract")?.Value == "True" 
                             || defElement.Element("Abstract")?.Value == "True";

            if (!string.IsNullOrEmpty(defName))
            {
                RimWorldDef rimDef = new()
                {
                    DefName = defName,
                    Type = defType,
                    Parent = defElement.Attribute("ParentName")?.Value 
                        ?? defElement.Element("ParentName")?.Value,
                    Abstract = isAbstract,
                    Content = defElement,
                    OriginalContent = null,
                    FilePath = Path.GetRelativePath(mod.Path, filePath),
                    Mod = mod,
                    OutgoingRefs = [],
                    IncomingRefs = [],
                    PatchHistory = [],
                    Conflicts = []
                };

                if (data.Defs.TryGetValue(defName, out var existing) && existing.Mod.PackageId != mod.PackageId)
                {
                    DefConflict conflict = new()
                    {
                        Type = ConflictType.Override,
                        Severity = ConflictSeverity.Warning,
                        DefName = defName,
                        Mods = [existing.Mod, mod],
                        Description = $"{mod.Name} overrides {defName} from {existing.Mod.Name}",
                        Resolution = mod.LoadOrder > existing.Mod.LoadOrder
                            ? $"{mod.Name} wins due to load order"
                            : $"{existing.Mod.Name} wins due to load order"
                    };

                    rimDef.Conflicts.Add(conflict);
                    lock (_dataLock)
                    {
                        data.Conflicts.Add(conflict);
                    }
                }

                lock (_dataLock)
                {
                    if (isAbstract)
                    {
                        data.AbstractDefs[defName] = rimDef;
                    }

                    if (existing == null || existing.Mod.LoadOrder < mod.LoadOrder)
                    {
                        data.Defs[defName] = rimDef;

                        if (!data.DefsByType.ContainsKey(defType))
                        {
                            data.DefsByType[defType] = [];
                        }

                        if (existing != null)
                        {
                            var typeList = data.DefsByType[defType];
                            var index = typeList.FindIndex(d => d.DefName == defName);
                            if (index != -1)
                            {
                                typeList.RemoveAt(index);
                            }
                        }

                        data.DefsByType[defType].Add(rimDef);

                        if (!data.DefsByMod.ContainsKey(mod.PackageId))
                        {
                            data.DefsByMod[mod.PackageId] = [];
                        }
                        data.DefsByMod[mod.PackageId].Add(rimDef);
                    }
                }
            }
        }

        return Task.CompletedTask;
    }

    private Task ProcessPatchFileAsync(string filePath, ModInfo mod, ServerData data)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            var doc = XDocument.Parse(content);

            if (doc.Root == null)
            {
                _logger.LogWarning("Patch file {FilePath} has no root element", filePath);
                return Task.CompletedTask;
            }

            // Parse patch operations from the XML
            foreach (var patchElement in doc.Root.Elements("Operation"))
            {
                var patch = ParsePatchOperation(patchElement, mod, filePath);
                if (patch.Id != string.Empty)
                {
                    var patchKey = $"{mod.PackageId}:{patch.Id}";
                    
                    lock (_dataLock)
                    {
                        if (!data.Patches.ContainsKey(patchKey))
                        {
                            data.Patches[patchKey] = [];
                        }
                        data.Patches[patchKey].Add(patch);
                        
                        // Also add to global patches list for cross-mod analysis
                        data.GlobalPatches.Add(patch);
                    }
                    
                    _logger.LogDebug("Found patch {PatchId} in {FilePath} targeting {Target}", 
                        patch.Id, filePath, patch.XPath);
                }
            }

            // Handle legacy patch format (direct operation elements)
            string[] operationTypes = ["PatchOperationAdd", "PatchOperationRemove", "PatchOperationReplace", 
                                     "PatchOperationTest", "PatchOperationConditional", "PatchOperationSequence"];
            
            foreach (var opType in operationTypes)
            {
                foreach (var opElement in doc.Root.Elements(opType))
                {
                    var patch = ParseLegacyPatchOperation(opElement, opType, mod, filePath);
                    if (patch.Id != string.Empty)
                    {
                        var patchKey = $"{mod.PackageId}:{patch.Id}";
                        
                        lock (_dataLock)
                        {
                            if (!data.Patches.ContainsKey(patchKey))
                            {
                                data.Patches[patchKey] = [];
                            }
                            data.Patches[patchKey].Add(patch);
                            data.GlobalPatches.Add(patch);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process patch file {FilePath}", filePath);
        }

        return Task.CompletedTask;
    }

    private PatchOperation ParsePatchOperation(XElement element, ModInfo mod, string filePath)
    {
        PatchOperation patch = new()
        {
            Id = element.Attribute("Class")?.Value ?? Path.GetFileNameWithoutExtension(filePath),
            Mod = mod,
            FilePath = filePath,
            XPath = element.Element("xpath")?.Value ?? string.Empty,
            Order = int.TryParse(element.Attribute("Order")?.Value, out var order) ? order : 0
        };

        // Parse operation type
        var className = element.Attribute("Class")?.Value;
        patch.Operation = className switch
        {
            "PatchOperationAdd" => PatchOperationType.PatchOperationAdd,
            "PatchOperationRemove" => PatchOperationType.PatchOperationRemove,
            "PatchOperationReplace" => PatchOperationType.PatchOperationReplace,
            "PatchOperationTest" => PatchOperationType.Test,
            "PatchOperationConditional" => PatchOperationType.Conditional,
            "PatchOperationSequence" => PatchOperationType.Sequence,
            _ => PatchOperationType.Replace
        };

        // Parse value content
        var valueElement = element.Element("value");
        if (valueElement != null)
        {
            patch.Value = valueElement.ToString();
        }

        // Parse conditions
        var conditionsElement = element.Element("conditions");
        if (conditionsElement != null)
        {
            patch.Conditions = new PatchConditions();
            
            foreach (var modLoadedElement in conditionsElement.Elements("modLoaded"))
            {
                var modId = modLoadedElement.Value;
                if (!string.IsNullOrEmpty(modId))
                {
                    patch.Conditions.ModLoaded.Add(modId);
                }
            }
            
            foreach (var modNotLoadedElement in conditionsElement.Elements("modNotLoaded"))
            {
                var modId = modNotLoadedElement.Value;
                if (!string.IsNullOrEmpty(modId))
                {
                    patch.Conditions.ModNotLoaded.Add(modId);
                }
            }
        }

        return patch;
    }

    private PatchOperation ParseLegacyPatchOperation(XElement element, string operationType, ModInfo mod, string filePath)
    {
        PatchOperation patch = new()
        {
            Id = $"{operationType}_{Guid.NewGuid()}",
            Mod = mod,
            FilePath = filePath,
            XPath = element.Element("xpath")?.Value ?? string.Empty,
            Order = 0
        };

        patch.Operation = operationType switch
        {
            "PatchOperationAdd" => PatchOperationType.PatchOperationAdd,
            "PatchOperationRemove" => PatchOperationType.PatchOperationRemove,
            "PatchOperationReplace" => PatchOperationType.PatchOperationReplace,
            "PatchOperationTest" => PatchOperationType.Test,
            "PatchOperationConditional" => PatchOperationType.Conditional,
            "PatchOperationSequence" => PatchOperationType.Sequence,
            _ => PatchOperationType.Replace
        };

        var valueElement = element.Element("value");
        if (valueElement != null)
        {
            patch.Value = valueElement.ToString();
        }

        return patch;
    }

    private List<string> ParseLoadFolders(string modPath, string rimworldVersion)
    {
        var loadFoldersPath = Path.Combine(modPath, "LoadFolders.xml");
        
        if (!File.Exists(loadFoldersPath))
        {
            List<string> defaultFolders = [];
            
            // Check version-specific folder first
            if (Directory.Exists(Path.Combine(modPath, rimworldVersion)))
            {
                defaultFolders.Add(rimworldVersion);
            }
            
            // Check if root has content (Assemblies, Defs, etc.)
            if (Directory.Exists(Path.Combine(modPath, "Assemblies")) || 
                Directory.Exists(Path.Combine(modPath, "Defs")) ||
                Directory.Exists(Path.Combine(modPath, "Patches")))
            {
                defaultFolders.Add(".");
            }
            
            return defaultFolders.Count > 0 ? defaultFolders : ["."];
        }

        try
        {
            var xmlContent = File.ReadAllText(loadFoldersPath);
            var doc = XDocument.Parse(xmlContent);
            
            List<string> matchedFolders = [];
            var foundVersionMatch = false;
            
            // Parse LoadFolders.xml structure - look for version-specific entries
            foreach (var versionElement in doc.Root?.Elements("v") ?? [])
            {
                // Check if this version block matches our target version
                var versionMatches = CheckVersionCompatibility(versionElement, rimworldVersion);
                
                if (versionMatches)
                {
                    foundVersionMatch = true;
                    
                    foreach (var folderElement in versionElement.Elements("li"))
                    {
                        var folder = folderElement.Value?.Trim();
                        if (!string.IsNullOrEmpty(folder) && !matchedFolders.Contains(folder))
                        {
                            matchedFolders.Add(folder);
                        }
                    }
                }
            }
            
            // If no version-specific match found, look for default structure (direct <li> elements)
            if (!foundVersionMatch)
            {
                var defaultList = doc.Root?.Element("li")?.Parent;
                if (defaultList != null)
                {
                    foreach (var folderElement in defaultList.Elements("li"))
                    {
                        var folder = folderElement.Value?.Trim();
                        if (!string.IsNullOrEmpty(folder))
                        {
                            matchedFolders.Add(folder);
                        }
                    }
                }
            }
            
            // If still no folders found, fallback to default behavior
            if (matchedFolders.Count == 0)
            {
                _logger.LogDebug("No matching folders in LoadFolders.xml for version {Version}, using defaults", rimworldVersion);
                if (Directory.Exists(Path.Combine(modPath, rimworldVersion)))
                {
                    matchedFolders.Add(rimworldVersion);
                }
                matchedFolders.Add(".");
            }
            
            return matchedFolders;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse LoadFolders.xml for mod {ModPath}, using default", modPath);
            return ["."];
        }
    }
    
    private bool CheckVersionCompatibility(XElement versionElement, string targetVersion)
    {
        // Get all version attributes
        var minVersion = versionElement.Attribute("min")?.Value;
        var maxVersion = versionElement.Attribute("max")?.Value;
        var exactVersion = versionElement.Attribute("v")?.Value;
        
        // If exact version specified, check for exact match
        if (!string.IsNullOrEmpty(exactVersion))
        {
            return string.Equals(exactVersion, targetVersion, StringComparison.OrdinalIgnoreCase);
        }
        
        // If min/max specified, check range (simplified version comparison)
        if (!string.IsNullOrEmpty(minVersion) || !string.IsNullOrEmpty(maxVersion))
        {
            var targetVersionNum = ParseVersionNumber(targetVersion);
            
            if (!string.IsNullOrEmpty(minVersion))
            {
                var minVersionNum = ParseVersionNumber(minVersion);
                if (targetVersionNum < minVersionNum)
                    return false;
            }
            
            if (!string.IsNullOrEmpty(maxVersion))
            {
                var maxVersionNum = ParseVersionNumber(maxVersion);
                if (targetVersionNum > maxVersionNum)
                    return false;
            }
            
            return true;
        }
        
        // No version constraints - this version block applies to all versions
        return true;
    }
    
    private double ParseVersionNumber(string version)
    {
        // Simple version parsing - handle formats like "1.6", "1.5.1", etc.
        if (string.IsNullOrEmpty(version))
            return 0.0;
            
        // Extract just the major.minor part for comparison
        var parts = version.Split('.');
        if (parts.Length >= 2 && 
            double.TryParse($"{parts[0]}.{parts[1]}", out var result))
        {
            return result;
        }
        
        if (double.TryParse(parts[0], out result))
        {
            return result;
        }
        
        return 0.0;
    }
}
