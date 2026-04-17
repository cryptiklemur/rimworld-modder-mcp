using System.Xml.Linq;

namespace RimWorldModderMcp.Models;

public class RimWorldDef
{
    public string DefName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Parent { get; set; }
    public bool Abstract { get; set; }
    public XElement Content { get; set; } = new("Empty");
    public XElement? OriginalContent { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public ModInfo Mod { get; set; } = new();
    public HashSet<DefReference> OutgoingRefs { get; set; } = [];
    public HashSet<DefReference> IncomingRefs { get; set; } = [];
    public List<PatchOperation> PatchHistory { get; set; } = [];
    public List<DefConflict> Conflicts { get; set; } = [];
}