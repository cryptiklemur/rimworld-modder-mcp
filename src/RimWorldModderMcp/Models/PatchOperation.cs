namespace RimWorldModderMcp.Models;

public enum PatchOperationType
{
    Add,
    Remove,
    Replace,
    AddModExtension,
    SetName,
    Sequence,
    Test,
    Conditional,
    PatchOperationFindMod,
    PatchOperationReplace,
    PatchOperationAdd,
    PatchOperationRemove
}

public class PatchConditions
{
    public List<string> ModLoaded { get; set; } = [];
    public List<string> ModNotLoaded { get; set; } = [];
}

public class PatchOperation
{
    public string Id { get; set; } = string.Empty;
    public ModInfo Mod { get; set; } = new();
    public string FilePath { get; set; } = string.Empty;
    public PatchOperationType Operation { get; set; }
    public string XPath { get; set; } = string.Empty;
    public object? Value { get; set; }
    public bool? Success { get; set; }
    public string? Error { get; set; }
    public string? TargetDef { get; set; }
    public int Order { get; set; }
    public PatchConditions? Conditions { get; set; }
    public HashSet<string> AppliedTo { get; set; } = [];
}