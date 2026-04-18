using System.CommandLine;
using System.CommandLine.Parsing;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Services;

namespace RimWorldModderMcp;

internal static class Program
{
    static async Task<int> Main(string[] args)
    {
        var rootCommand = new RootCommand("RimWorld Modder MCP - Analyze RimWorld defs, patches, compatibility, and modding workflows");

        var configOption = new Option<string>(
            name: "--config",
            description: "Path to a .rimworld-modder-mcp.json project config file");

        var projectRootOption = new Option<string>(
            name: "--project-root",
            description: "Project root used for config discovery and relative path resolution");

        var rimworldPathOption = new Option<string>(
            name: "--rimworld-path",
            description: "Path to the RimWorld installation");

        var modDirsOption = new Option<string[]>(
            name: "--mod-dirs",
            description: "Comma-separated list of mod directories to scan",
            getDefaultValue: () => Array.Empty<string>());

        var modsConfigPathOption = new Option<string>(
            name: "--mods-config-path",
            description: "Path to ModsConfig.xml");

        var serverNameOption = new Option<string>(
            name: "--server-name",
            description: "Server name",
            getDefaultValue: () => "rimworld-modder");

        var serverVersionOption = new Option<string>(
            name: "--server-version",
            description: "Server version",
            getDefaultValue: GetDefaultServerVersion);

        var modConcurrencyOption = new Option<int>(
            name: "--mod-concurrency",
            description: "Number of mods to process simultaneously",
            getDefaultValue: () => Math.Max(1, Environment.ProcessorCount / 4));

        var modBatchSizeOption = new Option<int>(
            name: "--mod-batch-size",
            description: "Number of mods to load in parallel",
            getDefaultValue: () => Math.Max(4, Environment.ProcessorCount / 2));

        var logLevelOption = new Option<string>(
            name: "--log-level",
            description: "Logging level: Debug, Information, Warning, Error",
            getDefaultValue: () => "Information");

        var rimworldVersionOption = new Option<string>(
            name: "--rimworld-version",
            description: "RimWorld version for mod compatibility",
            getDefaultValue: () => "1.6");

        var toolOption = new Option<string>(
            name: "--tool",
            description: "Execute a specific tool instead of starting the MCP server");

        var genericParamOption = new Option<string[]>(
            name: "--param",
            description: "Generic tool argument in key=value form; repeat for multiple arguments",
            getDefaultValue: () => Array.Empty<string>());

        var outputModeOption = new Option<string>(
            name: "--output-mode",
            description: "Output mode: compact, normal, or detailed");

        var pageSizeOption = new Option<int>(
            name: "--page-size",
            description: "Maximum items returned per array-valued section");

        var pageOffsetOption = new Option<int>(
            name: "--page-offset",
            description: "Offset to apply before paging array-valued sections");

        var handleResultsOption = new Option<bool>(
            name: "--handle-results",
            description: "Store a retrievable result handle for this response");

        var handleOption = new Option<string>(
            name: "--handle",
            description: "Stored result handle for get_result_by_handle");

        var defNameOption = new Option<string>(
            name: "--defName",
            description: "Definition name");

        var typeOption = new Option<string>(
            name: "--type",
            description: "Definition type");

        var searchTermOption = new Option<string>(
            name: "--searchTerm",
            description: "Search term");

        var inTypeOption = new Option<string>(
            name: "--inType",
            description: "Type filter");

        var maxResultsOption = new Option<int>(
            name: "--maxResults",
            description: "Maximum results to return",
            getDefaultValue: () => 100);

        var modPackageIdOption = new Option<string>(
            name: "--modPackageId",
            description: "Mod package ID");

        var conflictTypeOption = new Option<string>(
            name: "--conflictType",
            description: "Conflict type");

        var severityOption = new Option<string>(
            name: "--severity",
            description: "Minimum severity filter");

        var includeInactiveOption = new Option<bool>(
            name: "--includeInactive",
            description: "Include references from inactive mods");

        var xpathOption = new Option<string>(
            name: "--xpath",
            description: "XPath expression");

        var defName1Option = new Option<string>(
            name: "--defName1",
            description: "First definition name");

        var defName2Option = new Option<string>(
            name: "--defName2",
            description: "Second definition name");

        var modPackageId1Option = new Option<string>(
            name: "--modPackageId1",
            description: "First mod package ID");

        var modPackageId2Option = new Option<string>(
            name: "--modPackageId2",
            description: "Second mod package ID");

        var similarityThresholdOption = new Option<double>(
            name: "--similarityThreshold",
            description: "Similarity threshold",
            getDefaultValue: () => 0.9);

        var languageOption = new Option<string>(
            name: "--language",
            description: "Language");

        var assetTypeOption = new Option<string>(
            name: "--assetType",
            description: "Asset type",
            getDefaultValue: () => "all");

        var severityLevelOption = new Option<string>(
            name: "--severityLevel",
            description: "Severity level",
            getDefaultValue: () => "warning");

        var formatOption = new Option<string>(
            name: "--format",
            description: "Export or documentation format",
            getDefaultValue: () => "json");

        var maxDefinitionsOption = new Option<int>(
            name: "--maxDefinitions",
            description: "Maximum definitions to export",
            getDefaultValue: () => 1000);

        var focusStatOption = new Option<string>(
            name: "--focusStat",
            description: "Stat to focus analysis on");

        var directionOption = new Option<string>(
            name: "--direction",
            description: "Chain direction",
            getDefaultValue: () => "both");

        var maxDepthOption = new Option<int>(
            name: "--maxDepth",
            description: "Maximum chain depth",
            getDefaultValue: () => 5);

        var targetResearchOption = new Option<string>(
            name: "--targetResearch",
            description: "Target research");

        var includeAllPathsOption = new Option<bool>(
            name: "--includeAllPaths",
            description: "Include all possible paths");

        var biomeDefNameOption = new Option<string>(
            name: "--biomeDefName",
            description: "Biome definition name");

        var contentTypeOption = new Option<string>(
            name: "--contentType",
            description: "Content type",
            getDefaultValue: () => "all");

        var targetDefNameOption = new Option<string>(
            name: "--targetDefName",
            description: "Target definition name");

        var includeComfortOption = new Option<bool>(
            name: "--includeComfort",
            description: "Include comfort calculations",
            getDefaultValue: () => true);

        var logPathOption = new Option<string>(
            name: "--logPath",
            description: "Path to Player.log or Player-prev.log");

        var otherLogPathOption = new Option<string>(
            name: "--otherLogPath",
            description: "Second log path for compare_player_logs");

        var allowedDlcsOption = new Option<string>(
            name: "--allowedDlcs",
            description: "Comma-separated official content set to allow",
            getDefaultValue: () => "Core,Biotech");

        var scopeTypeOption = new Option<string>(
            name: "--scopeType",
            description: "Scope type: mod, def, def_type, or path");

        var scopeValueOption = new Option<string>(
            name: "--scopeValue",
            description: "Scope value");

        var referenceOption = new Option<string>(
            name: "--reference",
            description: "Broken reference or target DefName to explain");

        var baseRefOption = new Option<string>(
            name: "--baseRef",
            description: "Git base ref for changed-file tools");

        var pathsOption = new Option<string[]>(
            name: "--paths",
            description: "Comma-separated file paths to audit instead of discovering changed files",
            getDefaultValue: () => Array.Empty<string>());

        var moveBeforeModPackageIdOption = new Option<string>(
            name: "--moveBeforeModPackageId",
            description: "Simulate moving the mod before this mod package ID");

        var moveAfterModPackageIdOption = new Option<string>(
            name: "--moveAfterModPackageId",
            description: "Simulate moving the mod after this mod package ID");

        var options = new Option[]
        {
            configOption,
            projectRootOption,
            rimworldPathOption,
            modDirsOption,
            modsConfigPathOption,
            serverNameOption,
            serverVersionOption,
            modConcurrencyOption,
            modBatchSizeOption,
            logLevelOption,
            rimworldVersionOption,
            toolOption,
            genericParamOption,
            outputModeOption,
            pageSizeOption,
            pageOffsetOption,
            handleResultsOption,
            handleOption,
            defNameOption,
            typeOption,
            searchTermOption,
            inTypeOption,
            maxResultsOption,
            modPackageIdOption,
            conflictTypeOption,
            severityOption,
            includeInactiveOption,
            xpathOption,
            defName1Option,
            defName2Option,
            modPackageId1Option,
            modPackageId2Option,
            similarityThresholdOption,
            languageOption,
            assetTypeOption,
            severityLevelOption,
            formatOption,
            maxDefinitionsOption,
            focusStatOption,
            directionOption,
            maxDepthOption,
            targetResearchOption,
            includeAllPathsOption,
            biomeDefNameOption,
            contentTypeOption,
            targetDefNameOption,
            includeComfortOption,
            logPathOption,
            otherLogPathOption,
            allowedDlcsOption,
            scopeTypeOption,
            scopeValueOption,
            referenceOption,
            baseRefOption,
            pathsOption,
            moveBeforeModPackageIdOption,
            moveAfterModPackageIdOption
        };

        foreach (var option in options)
        {
            rootCommand.AddOption(option);
        }

        rootCommand.SetHandler(async context =>
        {
            var parseResult = context.ParseResult;

            ProjectContext projectContext;
            try
            {
                projectContext = BuildProjectContext(
                    parseResult,
                    configOption,
                    projectRootOption,
                    rimworldPathOption,
                    modDirsOption,
                    modsConfigPathOption,
                    logPathOption,
                    allowedDlcsOption,
                    outputModeOption,
                    pageSizeOption,
                    pageOffsetOption,
                    handleResultsOption,
                    rimworldVersionOption,
                    modConcurrencyOption,
                    modBatchSizeOption,
                    logLevelOption);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                context.ExitCode = 1;
                return;
            }

            var toolName = parseResult.GetValueForOption(toolOption);
            var toolExecutionOptions = new ToolExecutionOptions
            {
                OutputMode = ToolOutputFormatter.ParseMode(projectContext.OutputMode),
                PageSize = projectContext.PageSize,
                PageOffset = projectContext.PageOffset,
                HandleResults = projectContext.HandleResults
            };

            if (string.IsNullOrWhiteSpace(toolName))
            {
                if (string.IsNullOrWhiteSpace(projectContext.RimworldPath))
                {
                    Console.Error.WriteLine("Missing RimWorld path. Provide --rimworld-path or set rimworldPath in .rimworld-modder-mcp.json.");
                    context.ExitCode = 1;
                    return;
                }

                await RunServerAsync(
                    projectContext,
                    parseResult.GetValueForOption(serverNameOption)!,
                    parseResult.GetValueForOption(serverVersionOption)!);
                return;
            }

            if (ToolRequiresLoadedData(toolName) && string.IsNullOrWhiteSpace(projectContext.RimworldPath))
            {
                Console.Error.WriteLine($"Tool '{toolName}' requires RimWorld data. Provide --rimworld-path or set rimworldPath in .rimworld-modder-mcp.json.");
                context.ExitCode = 1;
                return;
            }

            var toolArgs = BuildToolArguments(
                toolName,
                parseResult,
                handleOption,
                defNameOption,
                typeOption,
                searchTermOption,
                inTypeOption,
                maxResultsOption,
                modPackageIdOption,
                conflictTypeOption,
                severityOption,
                includeInactiveOption,
                xpathOption,
                defName1Option,
                defName2Option,
                modPackageId1Option,
                modPackageId2Option,
                similarityThresholdOption,
                languageOption,
                assetTypeOption,
                severityLevelOption,
                formatOption,
                maxDefinitionsOption,
                focusStatOption,
                directionOption,
                maxDepthOption,
                targetResearchOption,
                includeAllPathsOption,
                biomeDefNameOption,
                contentTypeOption,
                targetDefNameOption,
                includeComfortOption,
                logPathOption,
                otherLogPathOption,
                allowedDlcsOption,
                scopeTypeOption,
                scopeValueOption,
                referenceOption,
                baseRefOption,
                pathsOption,
                moveBeforeModPackageIdOption,
                moveAfterModPackageIdOption,
                genericParamOption);

            await RunToolAsync(projectContext, toolName, toolArgs, toolExecutionOptions);
        });

        return await rootCommand.InvokeAsync(args);
    }

    private static ProjectContext BuildProjectContext(
        ParseResult parseResult,
        Option<string> configOption,
        Option<string> projectRootOption,
        Option<string> rimworldPathOption,
        Option<string[]> modDirsOption,
        Option<string> modsConfigPathOption,
        Option<string> logPathOption,
        Option<string> allowedDlcsOption,
        Option<string> outputModeOption,
        Option<int> pageSizeOption,
        Option<int> pageOffsetOption,
        Option<bool> handleResultsOption,
        Option<string> rimworldVersionOption,
        Option<int> modConcurrencyOption,
        Option<int> modBatchSizeOption,
        Option<string> logLevelOption)
    {
        var explicitConfigPath = parseResult.GetValueForOption(configOption);
        var explicitProjectRoot = parseResult.GetValueForOption(projectRootOption);
        var (config, configPath) = ProjectConfigLoader.Load(explicitConfigPath, explicitProjectRoot);

        var discoveryRoot = !string.IsNullOrWhiteSpace(explicitProjectRoot)
            ? Path.GetFullPath(explicitProjectRoot)
            : Directory.GetCurrentDirectory();

        var configBaseDirectory = configPath != null
            ? Path.GetDirectoryName(configPath)!
            : discoveryRoot;

        var projectRoot = ResolveProjectRoot(
            parseResult,
            projectRootOption,
            config?.ProjectRoot,
            configBaseDirectory,
            discoveryRoot);

        var cliModDirs = SplitOptionValues(parseResult.GetValueForOption(modDirsOption));

        return new ProjectContext
        {
            ConfigPath = configPath,
            ProjectRoot = projectRoot,
            RimworldPath = ResolveOptionalPath(
                WasOptionProvided(parseResult, rimworldPathOption)
                    ? parseResult.GetValueForOption(rimworldPathOption)
                    : config?.RimworldPath,
                projectRoot),
            ModDirs = cliModDirs.Count > 0
                ? ProjectConfigLoader.ResolvePaths(cliModDirs, projectRoot)
                : ProjectConfigLoader.ResolvePaths(config?.ModDirs, projectRoot),
            ModsConfigPath = ResolveOptionalPath(
                WasOptionProvided(parseResult, modsConfigPathOption)
                    ? parseResult.GetValueForOption(modsConfigPathOption)
                    : config?.ModsConfigPath,
                projectRoot),
            LogPath = ResolveOptionalPath(
                WasOptionProvided(parseResult, logPathOption)
                    ? parseResult.GetValueForOption(logPathOption)
                    : config?.LogPath,
                projectRoot),
            AllowedDlcs = WasOptionProvided(parseResult, allowedDlcsOption)
                ? parseResult.GetValueForOption(allowedDlcsOption)!
                : config?.AllowedDlcs ?? "Core,Biotech",
            OutputMode = WasOptionProvided(parseResult, outputModeOption)
                ? parseResult.GetValueForOption(outputModeOption)!
                : config?.OutputMode ?? "normal",
            PageSize = WasOptionProvided(parseResult, pageSizeOption)
                ? parseResult.GetValueForOption(pageSizeOption)
                : config?.PageSize ?? 25,
            PageOffset = WasOptionProvided(parseResult, pageOffsetOption)
                ? parseResult.GetValueForOption(pageOffsetOption)
                : config?.PageOffset ?? 0,
            HandleResults = WasOptionProvided(parseResult, handleResultsOption)
                ? parseResult.GetValueForOption(handleResultsOption)
                : config?.HandleResults ?? false,
            RimworldVersion = WasOptionProvided(parseResult, rimworldVersionOption)
                ? parseResult.GetValueForOption(rimworldVersionOption)!
                : config?.RimworldVersion ?? "1.6",
            ModConcurrency = WasOptionProvided(parseResult, modConcurrencyOption)
                ? parseResult.GetValueForOption(modConcurrencyOption)
                : config?.ModConcurrency ?? Math.Max(1, Environment.ProcessorCount / 4),
            ModBatchSize = WasOptionProvided(parseResult, modBatchSizeOption)
                ? parseResult.GetValueForOption(modBatchSizeOption)
                : config?.ModBatchSize ?? Math.Max(4, Environment.ProcessorCount / 2),
            LogLevel = WasOptionProvided(parseResult, logLevelOption)
                ? parseResult.GetValueForOption(logLevelOption)!
                : config?.LogLevel ?? "Information"
        };
    }

    private static string ResolveProjectRoot(
        ParseResult parseResult,
        Option<string> projectRootOption,
        string? configuredProjectRoot,
        string configBaseDirectory,
        string discoveryRoot)
    {
        if (WasOptionProvided(parseResult, projectRootOption))
        {
            return Path.GetFullPath(parseResult.GetValueForOption(projectRootOption)!);
        }

        if (!string.IsNullOrWhiteSpace(configuredProjectRoot))
        {
            return ProjectConfigLoader.ResolvePath(configuredProjectRoot, configBaseDirectory);
        }

        return discoveryRoot;
    }

    private static Dictionary<string, object?> BuildToolArguments(
        string toolName,
        ParseResult parseResult,
        Option<string> handleOption,
        Option<string> defNameOption,
        Option<string> typeOption,
        Option<string> searchTermOption,
        Option<string> inTypeOption,
        Option<int> maxResultsOption,
        Option<string> modPackageIdOption,
        Option<string> conflictTypeOption,
        Option<string> severityOption,
        Option<bool> includeInactiveOption,
        Option<string> xpathOption,
        Option<string> defName1Option,
        Option<string> defName2Option,
        Option<string> modPackageId1Option,
        Option<string> modPackageId2Option,
        Option<double> similarityThresholdOption,
        Option<string> languageOption,
        Option<string> assetTypeOption,
        Option<string> severityLevelOption,
        Option<string> formatOption,
        Option<int> maxDefinitionsOption,
        Option<string> focusStatOption,
        Option<string> directionOption,
        Option<int> maxDepthOption,
        Option<string> targetResearchOption,
        Option<bool> includeAllPathsOption,
        Option<string> biomeDefNameOption,
        Option<string> contentTypeOption,
        Option<string> targetDefNameOption,
        Option<bool> includeComfortOption,
        Option<string> logPathOption,
        Option<string> otherLogPathOption,
        Option<string> allowedDlcsOption,
        Option<string> scopeTypeOption,
        Option<string> scopeValueOption,
        Option<string> referenceOption,
        Option<string> baseRefOption,
        Option<string[]> pathsOption,
        Option<string> moveBeforeModPackageIdOption,
        Option<string> moveAfterModPackageIdOption,
        Option<string[]> genericParamOption)
    {
        var arguments = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        AddIfProvided(arguments, parseResult, handleOption, "handle");
        AddIfProvided(arguments, parseResult, defNameOption, "defName");
        AddIfProvided(arguments, parseResult, typeOption, "type");
        AddIfProvided(arguments, parseResult, searchTermOption, "searchTerm");
        AddIfProvided(arguments, parseResult, inTypeOption, "inType");
        AddIfProvided(arguments, parseResult, maxResultsOption, "maxResults");
        AddIfProvided(arguments, parseResult, modPackageIdOption, "modPackageId");
        AddIfProvided(arguments, parseResult, conflictTypeOption, "conflictType");
        AddIfProvided(arguments, parseResult, severityOption, "severity");
        AddIfProvided(arguments, parseResult, includeInactiveOption, "includeInactive");
        AddIfProvided(arguments, parseResult, xpathOption, "xpath");
        AddIfProvided(arguments, parseResult, defName1Option, "defName1");
        AddIfProvided(arguments, parseResult, defName2Option, "defName2");
        AddIfProvided(arguments, parseResult, modPackageId1Option, "modPackageId1");
        AddIfProvided(arguments, parseResult, modPackageId2Option, "modPackageId2");
        AddIfProvided(arguments, parseResult, similarityThresholdOption, "similarityThreshold");
        AddIfProvided(arguments, parseResult, languageOption, "language");
        AddIfProvided(arguments, parseResult, assetTypeOption, "assetType");
        AddIfProvided(arguments, parseResult, severityLevelOption, "severityLevel");
        AddIfProvided(arguments, parseResult, formatOption, "format");
        AddIfProvided(arguments, parseResult, maxDefinitionsOption, "maxDefinitions");
        AddIfProvided(arguments, parseResult, focusStatOption, "focusStat");
        AddIfProvided(arguments, parseResult, directionOption, "direction");
        AddIfProvided(arguments, parseResult, maxDepthOption, "maxDepth");
        AddIfProvided(arguments, parseResult, targetResearchOption, "targetResearch");
        AddIfProvided(arguments, parseResult, includeAllPathsOption, "includeAllPaths");
        AddIfProvided(arguments, parseResult, biomeDefNameOption, "biomeDefName");
        AddIfProvided(arguments, parseResult, contentTypeOption, "contentType");
        AddIfProvided(arguments, parseResult, targetDefNameOption, "targetDefName");
        AddIfProvided(arguments, parseResult, includeComfortOption, "includeComfort");
        AddIfProvided(arguments, parseResult, logPathOption, "logPath");
        AddIfProvided(arguments, parseResult, otherLogPathOption, "otherLogPath");
        AddIfProvided(arguments, parseResult, allowedDlcsOption, "allowedDlcs");
        AddIfProvided(arguments, parseResult, scopeTypeOption, "scopeType");
        AddIfProvided(arguments, parseResult, scopeValueOption, "scopeValue");
        AddIfProvided(arguments, parseResult, referenceOption, "reference");
        AddIfProvided(arguments, parseResult, baseRefOption, "baseRef");
        AddIfProvided(arguments, parseResult, moveBeforeModPackageIdOption, "moveBeforeModPackageId");
        AddIfProvided(arguments, parseResult, moveAfterModPackageIdOption, "moveAfterModPackageId");

        if (WasOptionProvided(parseResult, pathsOption))
        {
            var paths = SplitOptionValues(parseResult.GetValueForOption(pathsOption));
            if (paths.Count > 0)
            {
                arguments["paths"] = paths.ToArray();
            }
        }

        if (WasOptionProvided(parseResult, genericParamOption))
        {
            foreach (var entry in parseResult.GetValueForOption(genericParamOption) ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(entry))
                {
                    continue;
                }

                var separatorIndex = entry.IndexOf('=');
                if (separatorIndex <= 0 || separatorIndex == entry.Length - 1)
                {
                    throw new ArgumentException($"Invalid --param value '{entry}'. Use key=value.");
                }

                var key = entry[..separatorIndex].Trim();
                var value = entry[(separatorIndex + 1)..].Trim();

                if (arguments.TryGetValue(key, out var existing))
                {
                    if (existing is string[] array)
                    {
                        arguments[key] = array.Concat([value]).ToArray();
                    }
                    else if (existing is string stringValue)
                    {
                        arguments[key] = new[] { stringValue, value };
                    }
                    else
                    {
                        arguments[key] = value;
                    }
                }
                else
                {
                    arguments[key] = value;
                }
            }
        }

        switch (toolName.Trim().ToLowerInvariant())
        {
            case "find_duplicate_content":
            case "content_coverage_report":
                Alias(arguments, "type", "defType");
                break;

            case "triage_player_log":
            case "compare_player_logs":
            case "find_patch_hotspots":
                Alias(arguments, "maxResults", "maxGroups");
                break;

            case "scan_dlc_dependencies":
                Alias(arguments, "maxResults", "maxFindingsPerMod");
                break;

            case "audit_scope":
                Alias(arguments, "maxResults", "maxFindings");
                break;

            case "triage_patch_conflicts":
                Alias(arguments, "maxResults", "maxResults");
                break;

            case "mod_ready_check":
                Alias(arguments, "maxResults", "maxIssues");
                break;

            case "doctor":
            case "audit_changed_files":
            case "validate_changed_content":
            case "scope_search":
            case "load_order_impact_report":
                Alias(arguments, "maxResults", "maxResults");
                break;
        }

        return arguments;
    }

    private static void Alias(Dictionary<string, object?> arguments, string from, string to)
    {
        if (!arguments.ContainsKey(to) && arguments.TryGetValue(from, out var value))
        {
            arguments[to] = value;
        }
    }

    private static async Task RunServerAsync(ProjectContext projectContext, string serverName, string serverVersion)
    {
        var builder = CreateApplicationBuilder(projectContext);
        ConfigureCommonServices(builder.Services, projectContext);
        builder.Services.AddSingleton(sp => new FileWatcherService(
            sp.GetRequiredService<ILogger<FileWatcherService>>(),
            sp.GetRequiredService<ServerData>(),
            sp.GetRequiredService<ModLoader>(),
            sp.GetRequiredService<DefParser>()));
        builder.Services.AddHostedService<McpServer>();

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
        var serverData = host.Services.GetRequiredService<ServerData>();
        var modLoader = host.Services.GetRequiredService<ModLoader>();
        var defParser = host.Services.GetRequiredService<DefParser>();
        var conflictDetector = host.Services.GetRequiredService<ConflictDetector>();
        var fileWatcher = host.Services.GetRequiredService<FileWatcherService>();
        var mcpServer = host.Services.GetServices<IHostedService>().OfType<McpServer>().FirstOrDefault();

        try
        {
            logger.LogInformation("{Separator}", new string('=', 60));
            logger.LogInformation("{ServerName} v{ServerVersion}", serverName.ToUpperInvariant(), serverVersion);
            logger.LogInformation("{Separator}", new string('=', 60));
            logger.LogInformation("Project root: {ProjectRoot}", projectContext.ProjectRoot);

            var backgroundTask = Task.Run(async () =>
            {
                logger.LogInformation("🔄 Background loading started...");
                await RunBackgroundLoadingAsync(
                    logger,
                    modLoader,
                    defParser,
                    serverData,
                    conflictDetector,
                    fileWatcher,
                    projectContext.RimworldPath!,
                    projectContext.ModDirs,
                    projectContext.RimworldVersion,
                    projectContext.ModConcurrency);

                mcpServer?.NotifyInitializationComplete();
            });

            logger.LogInformation("🔌 MCP server ready for connections...");
            await host.RunAsync();

            logger.LogInformation("⏳ Waiting for background loading to complete...");
            await backgroundTask;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start server");
            Environment.Exit(1);
        }
    }

    private static async Task RunToolAsync(
        ProjectContext projectContext,
        string toolName,
        Dictionary<string, object?> toolArgs,
        ToolExecutionOptions options)
    {
        var builder = CreateApplicationBuilder(projectContext);
        ConfigureCommonServices(builder.Services, projectContext);
        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
        var serverData = host.Services.GetRequiredService<ServerData>();
        var modLoader = host.Services.GetRequiredService<ModLoader>();
        var defParser = host.Services.GetRequiredService<DefParser>();
        var conflictDetector = host.Services.GetRequiredService<ConflictDetector>();
        var toolExecutor = host.Services.GetRequiredService<ToolExecutor>();

        try
        {
            if (ToolRequiresLoadedData(toolName))
            {
                await LoadModsAndDefsAsync(logger, modLoader, defParser, serverData, projectContext.ModConcurrency, projectContext.RimworldVersion);
                conflictDetector.DetectAllConflicts(serverData);
                serverData.IsConflictsAnalyzed = true;
            }

            var execution = toolExecutor.Execute(toolName, toolArgs, options);
            Console.WriteLine(execution.Text);
        }
        catch (Exception ex)
        {
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { error = ex.Message }));
            Environment.Exit(1);
        }
    }

    private static HostApplicationBuilder CreateApplicationBuilder(ProjectContext projectContext)
    {
        var level = Enum.Parse<LogLevel>(projectContext.LogLevel, true);
        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => { options.LogToStandardErrorThreshold = LogLevel.Trace; });
        builder.Logging.SetMinimumLevel(level);
        return builder;
    }

    private static void ConfigureCommonServices(IServiceCollection services, ProjectContext projectContext)
    {
        services.AddSingleton(projectContext);
        services.AddSingleton(new ServerData());
        services.AddSingleton<ResultHandleStore>();
        services.AddSingleton(sp => new ModLoader(
            projectContext.RimworldPath ?? string.Empty,
            projectContext.ModBatchSize,
            projectContext.ModDirs,
            sp.GetRequiredService<ILogger<ModLoader>>()));
        services.AddSingleton<DefParser>();
        services.AddSingleton<ConflictDetector>();
        services.AddSingleton<ToolExecutor>();
    }

    private static async Task LoadModsAndDefsAsync(
        ILogger logger,
        ModLoader modLoader,
        DefParser defParser,
        ServerData serverData,
        int modConcurrency,
        string rimworldVersion)
    {
        logger.LogInformation("📁 Loading mods and scanning defs...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var modLoadingSw = System.Diagnostics.Stopwatch.StartNew();
        await modLoader.LoadModsAsync(serverData);
        serverData.IsModsLoaded = true;
        modLoadingSw.Stop();

        logger.LogInformation("📄 Scanning defs for {ModCount} mods...", serverData.Mods.Count);
        logger.LogInformation("   ⏱️  Mod loading took: {Duration:F2}s", modLoadingSw.Elapsed.TotalSeconds);

        var defScanningSw = System.Diagnostics.Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(modConcurrency);
        var defScanTasks = serverData.Mods.Values.Select(async mod =>
        {
            await semaphore.WaitAsync();
            try
            {
                await defParser.ScanModDefsAsync(mod, serverData, rimworldVersion);
            }
            finally
            {
                semaphore.Release();
            }
        }).ToList();

        await Task.WhenAll(defScanTasks);
        serverData.IsDefsLoaded = true;
        defScanningSw.Stop();
        sw.Stop();

        logger.LogInformation("   ⏱️  Def scanning took: {Duration:F2}s", defScanningSw.Elapsed.TotalSeconds);
        logger.LogInformation("   ✓ Completed mod loading + def scanning in {Duration:F2}s", sw.Elapsed.TotalSeconds);
    }

    private static async Task RunBackgroundLoadingAsync(
        ILogger logger,
        ModLoader modLoader,
        DefParser defParser,
        ServerData serverData,
        ConflictDetector conflictDetector,
        FileWatcherService fileWatcher,
        string rimworldPath,
        List<string> modDirs,
        string rimworldVersion,
        int modConcurrency)
    {
        try
        {
            logger.LogInformation(
                "⚙️  CPU Cores: {CpuCores}, Concurrency: Mod={ModConcurrency}, XML={XmlBatch}, ModLoad={ModBatch}",
                Environment.ProcessorCount,
                modConcurrency,
                16,
                16);

            var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var modLoadingStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await LoadModsAndDefsAsync(logger, modLoader, defParser, serverData, modConcurrency, rimworldVersion);
            modLoadingStopwatch.Stop();

            logger.LogInformation("🔍 Analyzing mod conflicts...");
            var conflictStopwatch = System.Diagnostics.Stopwatch.StartNew();
            conflictDetector.DetectAllConflicts(serverData);
            serverData.IsConflictsAnalyzed = true;
            conflictStopwatch.Stop();

            totalStopwatch.Stop();

            logger.LogInformation("   ✓ Loaded {ModCount} mods", serverData.Mods.Count);
            logger.LogInformation("   ✓ Found {DefCount} defs", serverData.Defs.Count);
            logger.LogInformation("⏱️  Performance Timings:");
            logger.LogInformation("   • Mod loading + def scanning: {Duration:F2}s", modLoadingStopwatch.Elapsed.TotalSeconds);
            logger.LogInformation("   • Conflict detection: {Duration:F2}s", conflictStopwatch.Elapsed.TotalSeconds);
            logger.LogInformation("   • Total loading: {Duration:F2}s", totalStopwatch.Elapsed.TotalSeconds);
            logger.LogInformation("{Separator}", new string('=', 60));
            logger.LogInformation("Background Loading Complete:");
            logger.LogInformation("  • Mods: {ModCount}", serverData.Mods.Count);
            logger.LogInformation("  • Defs: {DefCount}", serverData.Defs.Count);
            logger.LogInformation("  • Patches: {PatchCount}", serverData.GlobalPatches.Count);
            logger.LogInformation("  • Conflicts: {ConflictCount}", serverData.Conflicts.Count);
            logger.LogInformation("{Separator}", new string('=', 60));

            logger.LogInformation("👀 Starting file system watchers...");
            fileWatcher.StartWatching(rimworldPath, modDirs);
            logger.LogInformation("🎉 All data loading complete! Server fully operational.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background loading failed");
        }
    }

    private static bool ToolRequiresLoadedData(string toolName) =>
        toolName.Trim().ToLowerInvariant() switch
        {
            "doctor" => false,
            "triage_player_log" => false,
            "compare_player_logs" => false,
            "get_result_by_handle" => false,
            _ => true
        };

    private static void AddIfProvided(
        Dictionary<string, object?> arguments,
        ParseResult parseResult,
        Option<string> option,
        string parameterName)
    {
        if (!WasOptionProvided(parseResult, option))
        {
            return;
        }

        var value = parseResult.GetValueForOption(option);
        if (!string.IsNullOrWhiteSpace(value))
        {
            arguments[parameterName] = value;
        }
    }

    private static void AddIfProvided(
        Dictionary<string, object?> arguments,
        ParseResult parseResult,
        Option<int> option,
        string parameterName)
    {
        if (WasOptionProvided(parseResult, option))
        {
            arguments[parameterName] = parseResult.GetValueForOption(option);
        }
    }

    private static void AddIfProvided(
        Dictionary<string, object?> arguments,
        ParseResult parseResult,
        Option<double> option,
        string parameterName)
    {
        if (WasOptionProvided(parseResult, option))
        {
            arguments[parameterName] = parseResult.GetValueForOption(option);
        }
    }

    private static void AddIfProvided(
        Dictionary<string, object?> arguments,
        ParseResult parseResult,
        Option<bool> option,
        string parameterName)
    {
        if (WasOptionProvided(parseResult, option))
        {
            arguments[parameterName] = parseResult.GetValueForOption(option);
        }
    }

    private static bool WasOptionProvided<T>(ParseResult parseResult, Option<T> option) =>
        parseResult.FindResultFor(option) != null;

    private static List<string> SplitOptionValues(IEnumerable<string>? values)
    {
        if (values == null)
        {
            return [];
        }

        return values
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ResolveOptionalPath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return ProjectConfigLoader.ResolvePath(path, baseDirectory);
    }

    private static string GetDefaultServerVersion()
    {
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informationalVersion?.Split('+')[0] ?? "0.0.0-local";
    }
}
