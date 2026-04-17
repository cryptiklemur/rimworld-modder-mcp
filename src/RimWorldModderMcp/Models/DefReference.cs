namespace RimWorldModderMcp.Models;

public enum ReferenceType
{
    Parent,
    ThingDef,
    Recipe,
    Research,
    Building,
    Weapon,
    Apparel,
    Hediff,
    Trait,
    WorkGiver,
    Job,
    Thought,
    Interaction,
    Other
}

public class DefReference
{
    public string FromDef { get; set; } = string.Empty;
    public string ToDef { get; set; } = string.Empty;
    public ReferenceType ReferenceType { get; set; }
    public string Path { get; set; } = string.Empty;
    public string? Context { get; set; }
}