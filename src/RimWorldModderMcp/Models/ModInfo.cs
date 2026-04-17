namespace RimWorldModderMcp.Models;

public class ModInfo
{
    public string PackageId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Author { get; set; }
    public List<string> SupportedVersions { get; set; } = [];
    public int LoadOrder { get; set; }
    public string Path { get; set; } = string.Empty;
    public bool IsCore { get; set; }
    public bool IsDLC { get; set; }
    public List<string> Dependencies { get; set; } = [];
    public List<string> IncompatibleWith { get; set; } = [];
    public List<string> LoadBefore { get; set; } = [];
    public List<string> LoadAfter { get; set; } = [];
}