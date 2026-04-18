using System.Text.Json.Serialization;

namespace RimWorldModderMcp.Models;

public sealed class ProjectConfig
{
    [JsonPropertyName("rimworldPath")]
    public string? RimworldPath { get; set; }

    [JsonPropertyName("modDirs")]
    public List<string>? ModDirs { get; set; }

    [JsonPropertyName("modsConfigPath")]
    public string? ModsConfigPath { get; set; }

    [JsonPropertyName("logPath")]
    public string? LogPath { get; set; }

    [JsonPropertyName("allowedDlcs")]
    public string? AllowedDlcs { get; set; }

    [JsonPropertyName("projectRoot")]
    public string? ProjectRoot { get; set; }

    [JsonPropertyName("outputMode")]
    public string? OutputMode { get; set; }

    [JsonPropertyName("pageSize")]
    public int? PageSize { get; set; }

    [JsonPropertyName("pageOffset")]
    public int? PageOffset { get; set; }

    [JsonPropertyName("handleResults")]
    public bool? HandleResults { get; set; }

    [JsonPropertyName("rimworldVersion")]
    public string? RimworldVersion { get; set; }

    [JsonPropertyName("modConcurrency")]
    public int? ModConcurrency { get; set; }

    [JsonPropertyName("modBatchSize")]
    public int? ModBatchSize { get; set; }

    [JsonPropertyName("logLevel")]
    public string? LogLevel { get; set; }
}
