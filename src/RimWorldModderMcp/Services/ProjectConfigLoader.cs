using System.Text.Json;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public static class ProjectConfigLoader
{
    public const string DefaultConfigFileName = ".rimworld-modder-mcp.json";

    public static (ProjectConfig? config, string? configPath) Load(string? explicitConfigPath, string? explicitProjectRoot)
    {
        var configPath = ResolveConfigPath(explicitConfigPath, explicitProjectRoot);
        if (configPath == null)
        {
            return (null, null);
        }

        var contents = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<ProjectConfig>(contents, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        });

        return (config, configPath);
    }

    public static string? ResolveConfigPath(string? explicitConfigPath, string? explicitProjectRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitConfigPath))
        {
            var resolved = Path.GetFullPath(explicitConfigPath);
            if (!File.Exists(resolved))
            {
                throw new FileNotFoundException($"Project config '{resolved}' was not found.", resolved);
            }

            return resolved;
        }

        var searchRoot = !string.IsNullOrWhiteSpace(explicitProjectRoot)
            ? Path.GetFullPath(explicitProjectRoot)
            : Directory.GetCurrentDirectory();

        var current = new DirectoryInfo(searchRoot);
        while (current != null)
        {
            var candidate = Path.Combine(current.FullName, DefaultConfigFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        return null;
    }

    public static string ResolvePath(string? value, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return Path.IsPathRooted(value)
            ? Path.GetFullPath(value)
            : Path.GetFullPath(Path.Combine(baseDirectory, value));
    }

    public static List<string> ResolvePaths(IEnumerable<string>? values, string baseDirectory)
    {
        if (values == null)
        {
            return [];
        }

        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ResolvePath(value, baseDirectory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
