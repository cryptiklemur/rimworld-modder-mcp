namespace RimWorldModderMcp.Models;

public class ServerData
{
    public Dictionary<string, RimWorldDef> Defs { get; set; } = new();
    public Dictionary<string, List<RimWorldDef>> DefsByType { get; set; } = new();
    public Dictionary<string, List<RimWorldDef>> DefsByMod { get; set; } = new();
    public Dictionary<string, ModInfo> Mods { get; set; } = new();
    public Dictionary<string, HashSet<DefReference>> ReferenceGraph { get; set; } = new();
    public Dictionary<string, List<PatchOperation>> Patches { get; set; } = new();
    public List<PatchOperation> GlobalPatches { get; set; } = [];
    public List<DefConflict> Conflicts { get; set; } = [];
    public List<string> LoadOrder { get; set; } = [];
    public Dictionary<string, RimWorldDef> AbstractDefs { get; set; } = new();

    // Loading status
    public bool IsModsLoaded { get; set; } = false;
    public bool IsDefsLoaded { get; set; } = false;
    public bool IsConflictsAnalyzed { get; set; } = false;
    public bool IsFullyLoaded => IsModsLoaded && IsDefsLoaded && IsConflictsAnalyzed;
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
}
