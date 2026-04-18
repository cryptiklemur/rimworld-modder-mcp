namespace RimWorldModderMcp.Models;

public sealed class ProjectContext
{
    public string? ConfigPath { get; init; }
    public string ProjectRoot { get; init; } = Directory.GetCurrentDirectory();
    public string? RimworldPath { get; init; }
    public List<string> ModDirs { get; init; } = [];
    public string? ModsConfigPath { get; init; }
    public string? LogPath { get; init; }
    public string AllowedDlcs { get; init; } = "Core,Biotech";
    public string OutputMode { get; init; } = "normal";
    public int PageSize { get; init; } = 25;
    public int PageOffset { get; init; }
    public bool HandleResults { get; init; }
    public string RimworldVersion { get; init; } = "1.6";
    public int ModConcurrency { get; init; } = Math.Max(1, Environment.ProcessorCount / 4);
    public int ModBatchSize { get; init; } = Math.Max(4, Environment.ProcessorCount / 2);
    public string LogLevel { get; init; } = "Information";

    public bool HasProjectConfig => !string.IsNullOrWhiteSpace(ConfigPath);
}
