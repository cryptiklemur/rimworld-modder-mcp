using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public class FileWatcherService : IDisposable
{
    private readonly ILogger<FileWatcherService> _logger;
    private readonly ServerData _serverData;
    private readonly ModLoader _modLoader;
    private readonly DefParser _defParser;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentQueue<FileChangeEvent> _changeQueue = new();
    private readonly Timer _processTimer;
    private readonly SemaphoreSlim _processingSemaphore = new(1, 1);
    private bool _disposed;

    public FileWatcherService(
        ILogger<FileWatcherService> logger,
        ServerData serverData,
        ModLoader modLoader,
        DefParser defParser)
    {
        _logger = logger;
        _serverData = serverData;
        _modLoader = modLoader;
        _defParser = defParser;
        
        // Process changes every 2 seconds to batch operations
        _processTimer = new Timer(ProcessPendingChanges, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void StartWatching(string rimworldPath, List<string> modDirs)
    {
        _logger.LogInformation("🔍 Setting up file system watchers...");

        // Watch RimWorld core data
        SetupWatcher(Path.Combine(rimworldPath, "Data"), "RimWorld Core");

        // Watch all mod directories
        foreach (var modDir in modDirs)
        {
            if (Directory.Exists(modDir))
            {
                SetupWatcher(modDir, $"Mods ({modDir})");
            }
        }

        // Watch individual loaded mods
        foreach (var mod in _serverData.Mods.Values)
        {
            if (Directory.Exists(mod.Path))
            {
                SetupWatcher(mod.Path, $"Mod: {mod.Name}");
            }
        }

        _logger.LogInformation("   ✓ Watching {WatcherCount} directories", _watchers.Count);
    }

    private void SetupWatcher(string path, string description)
    {
        try
        {
            FileSystemWatcher watcher = new(path)
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | 
                              NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size
            };

            watcher.Created += (sender, e) => OnFileChanged(e, FileChangeType.Created, description);
            watcher.Changed += (sender, e) => OnFileChanged(e, FileChangeType.Modified, description);
            watcher.Deleted += (sender, e) => OnFileChanged(e, FileChangeType.Deleted, description);
            watcher.Renamed += (sender, e) => OnFileRenamed(e, description);

            _watchers.Add(watcher);
            _logger.LogDebug("Watching: {Description} at {Path}", description, path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to setup watcher for {Description} at {Path}", description, path);
        }
    }

    private void OnFileChanged(FileSystemEventArgs e, FileChangeType changeType, string source)
    {
        // Filter for relevant file types
        var extension = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (!IsRelevantFile(extension))
            return;

        _changeQueue.Enqueue(new FileChangeEvent
        {
            Path = e.FullPath,
            ChangeType = changeType,
            Source = source,
            Timestamp = DateTime.UtcNow
        });

        _logger.LogDebug("File {ChangeType}: {Path} ({Source})", changeType, e.FullPath, source);
    }

    private void OnFileRenamed(RenamedEventArgs e, string source)
    {
        var extension = Path.GetExtension(e.FullPath).ToLowerInvariant();
        if (!IsRelevantFile(extension))
            return;

        // Handle rename as delete + create
        _changeQueue.Enqueue(new FileChangeEvent
        {
            Path = e.OldFullPath,
            ChangeType = FileChangeType.Deleted,
            Source = source,
            Timestamp = DateTime.UtcNow
        });

        _changeQueue.Enqueue(new FileChangeEvent
        {
            Path = e.FullPath,
            ChangeType = FileChangeType.Created,
            Source = source,
            Timestamp = DateTime.UtcNow
        });

        _logger.LogDebug("File renamed: {OldPath} -> {NewPath} ({Source})", e.OldFullPath, e.FullPath, source);
    }

    private static bool IsRelevantFile(string extension)
    {
        return extension switch
        {
            ".xml" => true,  // Def files, LoadFolders.xml, About.xml
            _ => false
        };
    }

    private async void ProcessPendingChanges(object? state)
    {
        if (_changeQueue.IsEmpty || !await _processingSemaphore.WaitAsync(100))
            return;

        try
        {
            List<FileChangeEvent> changes = [];
            
            // Collect all pending changes
            while (_changeQueue.TryDequeue(out var change))
            {
                changes.Add(change);
            }

            if (changes.Count == 0)
                return;

            _logger.LogInformation("📁 Processing {ChangeCount} file changes...", changes.Count);

            // Group changes by type and mod
            var changesByMod = GroupChangesByMod(changes);
            
            foreach (var kvp in changesByMod)
            {
                var modId = kvp.Key;
                var modChanges = kvp.Value;

                await ProcessModChanges(modId, modChanges);
            }

            _logger.LogInformation("   ✓ Processed all file changes");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing file changes");
        }
        finally
        {
            _processingSemaphore.Release();
        }
    }

    private Dictionary<string, List<FileChangeEvent>> GroupChangesByMod(List<FileChangeEvent> changes)
    {
        Dictionary<string, List<FileChangeEvent>> grouped = [];

        foreach (var change in changes)
        {
            var modId = DetermineModId(change.Path);
            
            if (!grouped.ContainsKey(modId))
            {
                grouped[modId] = [];
            }
            
            grouped[modId].Add(change);
        }

        return grouped;
    }

    private string DetermineModId(string filePath)
    {
        // Check if it's a core RimWorld file
        foreach (var mod in _serverData.Mods.Values)
        {
            if (filePath.StartsWith(mod.Path, StringComparison.OrdinalIgnoreCase))
            {
                return mod.PackageId;
            }
        }

        // Default to "core" for RimWorld base files
        return "rimworld.core";
    }

    private async Task ProcessModChanges(string modId, List<FileChangeEvent> changes)
    {
        _logger.LogInformation("🔄 Processing changes for mod: {ModId}", modId);

        // Separate changes by file type
        List<FileChangeEvent> defChanges = [];
        List<FileChangeEvent> configChanges = [];

        foreach (var change in changes)
        {
            var extension = Path.GetExtension(change.Path).ToLowerInvariant();
            var fileName = Path.GetFileName(change.Path).ToLowerInvariant();

            if (fileName is "about.xml" or "loadfolders.xml")
            {
                configChanges.Add(change);
            }
            else if (extension == ".xml")
            {
                defChanges.Add(change);
            }
        }

        // Process configuration changes first (may affect how we process other files)
        if (configChanges.Count > 0)
        {
            await ProcessConfigChanges(modId, configChanges);
        }

        // Process def changes
        if (defChanges.Count > 0)
        {
            await ProcessDefChanges(modId, defChanges);
        }
    }

    private async Task ProcessConfigChanges(string modId, List<FileChangeEvent> changes)
    {
        _logger.LogDebug("Processing config changes for {ModId}", modId);

        // If About.xml or LoadFolders.xml changed, we may need to reload the entire mod
        var needsFullReload = changes.Any(c => 
            Path.GetFileName(c.Path).ToLowerInvariant() is "about.xml" or "loadfolders.xml");

        if (needsFullReload && _serverData.Mods.TryGetValue(modId, out var mod))
        {
            _logger.LogInformation("🔄 Full reload required for mod: {ModName}", mod.Name);
            
            // Remove existing data for this mod
            RemoveModData(modId);
            
            // Reload the mod
            await _modLoader.LoadModAsync(mod.Path, _serverData);
            
            // Rescan defs for this mod
            if (_serverData.Mods.TryGetValue(modId, out var reloadedMod))
            {
                await _defParser.ScanModDefsAsync(reloadedMod, _serverData);
            }
        }
    }

    private async Task ProcessDefChanges(string modId, List<FileChangeEvent> changes)
    {
        _logger.LogDebug("Processing def changes for {ModId}", modId);

        if (!_serverData.Mods.TryGetValue(modId, out var mod))
            return;

        // For def changes, rescan the entire mod to handle dependencies properly
        // Remove existing defs for this mod
        RemoveModDefs(modId);
        
        // Rescan all defs for this mod
        await _defParser.ScanModDefsAsync(mod, _serverData);
        
        _logger.LogInformation("🔄 Rescanned defs for mod: {ModName}", mod.Name);
    }

    private void RemoveModData(string modId)
    {
        // Remove defs
        RemoveModDefs(modId);
        
        // Remove mod from collections
        if (_serverData.DefsByMod.ContainsKey(modId))
        {
            _serverData.DefsByMod.Remove(modId);
        }
    }

    private void RemoveModDefs(string modId)
    {
        if (!_serverData.DefsByMod.TryGetValue(modId, out var modDefs))
            return;

        foreach (var def in modDefs)
        {
            // Remove from main defs collection
            _serverData.Defs.Remove(def.DefName);
            
            // Remove from abstract defs if applicable
            if (def.Abstract)
            {
                _serverData.AbstractDefs.Remove(def.DefName);
            }
            
            // Remove from type collections
            if (_serverData.DefsByType.TryGetValue(def.Type, out var typeDefs))
            {
                typeDefs.Remove(def);
                if (typeDefs.Count == 0)
                {
                    _serverData.DefsByType.Remove(def.Type);
                }
            }
        }

        _serverData.DefsByMod.Remove(modId);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _processTimer?.Dispose();
        
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        
        _watchers.Clear();
        _processingSemaphore.Dispose();
        _disposed = true;
    }
}

internal class FileChangeEvent
{
    public required string Path { get; init; }
    public required FileChangeType ChangeType { get; init; }
    public required string Source { get; init; }
    public required DateTime Timestamp { get; init; }
}

internal enum FileChangeType
{
    Created,
    Modified,
    Deleted
}
