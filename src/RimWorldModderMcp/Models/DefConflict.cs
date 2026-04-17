namespace RimWorldModderMcp.Models;

public enum ConflictType
{
    Override,
    PatchCollision,
    MissingDependency,
    CircularDependency,
    IncompatiblePatch,
    XPathConflict
}

public enum ConflictSeverity
{
    Error,
    Warning,
    Info
}

public class DefConflict
{
    public ConflictType Type { get; set; }
    public ConflictSeverity Severity { get; set; }
    public string? DefName { get; set; }
    public string? XPath { get; set; }
    public List<ModInfo> Mods { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public string? Resolution { get; set; }
}