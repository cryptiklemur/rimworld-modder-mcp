using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Services;
using RimWorldModderMcp.Tools.RimWorld;
using RimWorldModderMcp.Tools.Conflicts;
using RimWorldModderMcp.Tools.Statistics;
using RimWorldModderMcp.Tools.Patch;
using RimWorldModderMcp.Tools.Performance;
using RimWorldModderMcp.Tools.Development;
using RimWorldModderMcp.Tools.GameMechanics;

namespace RimWorldModderMcp;

class Program
{
    static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand = new("RimWorld Modder MCP - Analyze RimWorld defs, patches, compatibility, and modding workflows");

        Option<string> rimworldPathOption = new(
            name: "--rimworld-path",
            description: "Path to RimWorld installation")
        {
            IsRequired = true
        };

        Option<string[]> modDirsOption = new(
            name: "--mod-dirs",
            description: "Comma-separated list of mod directories to scan",
            getDefaultValue: () => Array.Empty<string>());

        Option<string> modsConfigPathOption = new(
            name: "--mods-config-path",
            description: "Path to ModsConfig.xml file to filter only enabled mods");

        Option<string> serverNameOption = new(
            name: "--server-name",
            description: "Server name",
            getDefaultValue: () => "rimworld-modder");

        Option<string> serverVersionOption = new(
            name: "--server-version",
            description: "Server version",
            getDefaultValue: GetDefaultServerVersion);

        Option<int> modConcurrencyOption = new(
            name: "--mod-concurrency",
            description: "Number of mods to process simultaneously",
            getDefaultValue: () => Environment.ProcessorCount / 4);

        Option<int> modBatchSizeOption = new(
            name: "--mod-batch-size",
            description: "Number of mods to load in parallel",
            getDefaultValue: () => Math.Max(4, Environment.ProcessorCount / 2));

        Option<string> logLevelOption = new(
            name: "--log-level",
            description: "Logging level: Debug, Information, Warning, Error",
            getDefaultValue: () => "Information");

        Option<string> rimworldVersionOption = new(
            name: "--rimworld-version",
            description: "RimWorld version for mod compatibility (affects which mod assemblies to include)",
            getDefaultValue: () => "1.6");

        Option<string> toolOption = new(
            name: "--tool",
            description: "Execute a specific tool instead of starting MCP server");

        Option<string> defNameOption = new(
            name: "--defName",
            description: "Definition name for get_def tool");

        Option<string> typeOption = new(
            name: "--type",
            description: "Type for get_defs_by_type tool");

        Option<string> searchTermOption = new(
            name: "--searchTerm",
            description: "Search term for search_defs tool");

        Option<string> inTypeOption = new(
            name: "--inType",
            description: "Type filter for search_defs tool");

        Option<int> maxResultsOption = new(
            name: "--maxResults",
            description: "Maximum results for search tools",
            getDefaultValue: () => 100);

        Option<string> modPackageIdOption = new(
            name: "--modPackageId",
            description: "Mod package ID for get_conflicts tool");

        Option<string> conflictTypeOption = new(
            name: "--conflictType",
            description: "Conflict type for get_conflicts tool");

        Option<string> severityOption = new(
            name: "--severity",
            description: "Severity for get_conflicts tool");

        Option<bool> includeInactiveOption = new(
            name: "--includeInactive",
            description: "Include references from inactive mods for get_references tool",
            getDefaultValue: () => false);

        Option<string> xpathOption = new(
            name: "--xpath",
            description: "XPath expression for validate_xpath tool");

        Option<string> defName1Option = new(
            name: "--defName1",
            description: "First definition name for compare_defs tool");

        Option<string> defName2Option = new(
            name: "--defName2", 
            description: "Second definition name for compare_defs tool");

        Option<string> modPackageId1Option = new(
            name: "--modPackageId1",
            description: "First mod package ID for mod analysis tools");

        Option<string> modPackageId2Option = new(
            name: "--modPackageId2",
            description: "Second mod package ID for mod analysis tools");

        Option<double> similarityThresholdOption = new(
            name: "--similarityThreshold",
            description: "Similarity threshold for find_duplicate_content tool (0.0-1.0)",
            getDefaultValue: () => 0.9);

        Option<string> languageOption = new(
            name: "--language",
            description: "Language for validate_localization tool");

        Option<string> assetTypeOption = new(
            name: "--assetType",
            description: "Asset type for find_unused_assets tool (textures, sounds, all)",
            getDefaultValue: () => "all");

        Option<string> severityLevelOption = new(
            name: "--severityLevel",
            description: "Severity level for lint_xml tool (info, warning, error)",
            getDefaultValue: () => "warning");

        Option<string> formatOption = new(
            name: "--format",
            description: "Format for export/generation tools",
            getDefaultValue: () => "json");

        Option<int> maxDefinitionsOption = new(
            name: "--maxDefinitions",
            description: "Maximum definitions for export_definitions tool",
            getDefaultValue: () => 1000);

        Option<string> focusStatOption = new(
            name: "--focusStat",
            description: "Stat to focus analysis on for analyze_balance tool");

        Option<string> directionOption = new(
            name: "--direction",
            description: "Chain direction for get_recipe_chains tool (ingredients, products, both)",
            getDefaultValue: () => "both");

        Option<int> maxDepthOption = new(
            name: "--maxDepth",
            description: "Maximum chain depth for get_recipe_chains tool",
            getDefaultValue: () => 5);

        Option<string> targetResearchOption = new(
            name: "--targetResearch",
            description: "Target research for find_research_paths tool");

        Option<bool> includeAllPathsOption = new(
            name: "--includeAllPaths",
            description: "Include all possible paths for find_research_paths tool",
            getDefaultValue: () => false);

        Option<string> biomeDefNameOption = new(
            name: "--biomeDefName",
            description: "Specific biome for get_biome_compatibility tool");

        Option<string> contentTypeOption = new(
            name: "--contentType",
            description: "Content type for biome analysis (animals, plants, terrain, all)",
            getDefaultValue: () => "all");

        Option<string> targetDefNameOption = new(
            name: "--targetDefName",
            description: "Target definition name for various tools");

        Option<bool> includeComfortOption = new(
            name: "--includeComfort",
            description: "Include comfort calculations for calculate_room_requirements tool",
            getDefaultValue: () => true);

        Option<string> logPathOption = new(
            name: "--logPath",
            description: "Player.log path for log and readiness tools");

        Option<string> allowedDlcsOption = new(
            name: "--allowedDlcs",
            description: "Comma-separated official content set to allow",
            getDefaultValue: () => "Core,Biotech");

        Option<string> scopeTypeOption = new(
            name: "--scopeType",
            description: "Scope type for audit_scope: mod, def, def_type, or path");

        Option<string> scopeValueOption = new(
            name: "--scopeValue",
            description: "Scope value for audit_scope");

        rootCommand.AddOption(rimworldPathOption);
        rootCommand.AddOption(modDirsOption);
        rootCommand.AddOption(modsConfigPathOption);
        rootCommand.AddOption(serverNameOption);
        rootCommand.AddOption(serverVersionOption);
        rootCommand.AddOption(modConcurrencyOption);
        rootCommand.AddOption(modBatchSizeOption);
        rootCommand.AddOption(logLevelOption);
        rootCommand.AddOption(rimworldVersionOption);
        rootCommand.AddOption(toolOption);
        rootCommand.AddOption(defNameOption);
        rootCommand.AddOption(typeOption);
        rootCommand.AddOption(searchTermOption);
        rootCommand.AddOption(inTypeOption);
        rootCommand.AddOption(maxResultsOption);
        rootCommand.AddOption(modPackageIdOption);
        rootCommand.AddOption(conflictTypeOption);
        rootCommand.AddOption(severityOption);
        rootCommand.AddOption(includeInactiveOption);
        rootCommand.AddOption(xpathOption);
        rootCommand.AddOption(defName1Option);
        rootCommand.AddOption(defName2Option);
        rootCommand.AddOption(modPackageId1Option);
        rootCommand.AddOption(modPackageId2Option);
        rootCommand.AddOption(similarityThresholdOption);
        rootCommand.AddOption(languageOption);
        rootCommand.AddOption(assetTypeOption);
        rootCommand.AddOption(severityLevelOption);
        rootCommand.AddOption(formatOption);
        rootCommand.AddOption(maxDefinitionsOption);
        rootCommand.AddOption(focusStatOption);
        rootCommand.AddOption(directionOption);
        rootCommand.AddOption(maxDepthOption);
        rootCommand.AddOption(targetResearchOption);
        rootCommand.AddOption(includeAllPathsOption);
        rootCommand.AddOption(biomeDefNameOption);
        rootCommand.AddOption(contentTypeOption);
        rootCommand.AddOption(targetDefNameOption);
        rootCommand.AddOption(includeComfortOption);
        rootCommand.AddOption(logPathOption);
        rootCommand.AddOption(allowedDlcsOption);
        rootCommand.AddOption(scopeTypeOption);
        rootCommand.AddOption(scopeValueOption);

        rootCommand.SetHandler(async (context) =>
        {
            var rimworldPath = context.ParseResult.GetValueForOption(rimworldPathOption)!;
            var modDirs = context.ParseResult.GetValueForOption(modDirsOption)!;
            var modsConfigPath = context.ParseResult.GetValueForOption(modsConfigPathOption);
            var serverName = context.ParseResult.GetValueForOption(serverNameOption)!;
            var serverVersion = context.ParseResult.GetValueForOption(serverVersionOption)!;
            var modConcurrency = context.ParseResult.GetValueForOption(modConcurrencyOption);
            var modBatchSize = context.ParseResult.GetValueForOption(modBatchSizeOption);
            var logLevel = context.ParseResult.GetValueForOption(logLevelOption)!;
            var rimworldVersion = context.ParseResult.GetValueForOption(rimworldVersionOption)!;
            var tool = context.ParseResult.GetValueForOption(toolOption);

            List<string> modDirsList = [];
            foreach (var dirs in modDirs)
            {
                modDirsList.AddRange(dirs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }

            // Check if tool mode is requested
            if (!string.IsNullOrEmpty(tool))
            {
                var toolArgs = new Dictionary<string, object?>();
                
                // Extract tool arguments based on tool type
                switch (tool)
                {
                    case "get_def":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "get_defs_by_type":
                        toolArgs["type"] = context.ParseResult.GetValueForOption(typeOption);
                        break;
                    case "search_defs":
                        toolArgs["searchTerm"] = context.ParseResult.GetValueForOption(searchTermOption);
                        toolArgs["inType"] = context.ParseResult.GetValueForOption(inTypeOption);
                        toolArgs["maxResults"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                    case "get_conflicts":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["conflictType"] = context.ParseResult.GetValueForOption(conflictTypeOption);
                        toolArgs["severity"] = context.ParseResult.GetValueForOption(severityOption);
                        break;
                    case "calculate_market_value":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "get_references":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        toolArgs["includeInactive"] = context.ParseResult.GetValueForOption(includeInactiveOption);
                        break;
                    case "get_def_dependencies":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "validate_def":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "validate_xpath":
                        toolArgs["xpath"] = context.ParseResult.GetValueForOption(xpathOption);
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "get_def_inheritance_tree":
                    case "get_patches_for_def":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "compare_defs":
                        toolArgs["defName1"] = context.ParseResult.GetValueForOption(defName1Option);
                        toolArgs["defName2"] = context.ParseResult.GetValueForOption(defName2Option);
                        break;
                    case "get_abstract_defs":
                        toolArgs["type"] = context.ParseResult.GetValueForOption(typeOption);
                        break;
                    case "analyze_mod_compatibility":
                        toolArgs["modPackageId1"] = context.ParseResult.GetValueForOption(modPackageId1Option);
                        toolArgs["modPackageId2"] = context.ParseResult.GetValueForOption(modPackageId2Option);
                        break;
                    case "get_mod_dependencies":
                    case "validate_mod_structure":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        break;
                    case "find_broken_references":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        break;
                    case "find_duplicate_content":
                        toolArgs["defType"] = context.ParseResult.GetValueForOption(typeOption);
                        toolArgs["similarityThreshold"] = context.ParseResult.GetValueForOption(similarityThresholdOption);
                        break;
                    case "suggest_optimizations":
                    case "analyze_texture_usage":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        break;
                    case "validate_localization":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["language"] = context.ParseResult.GetValueForOption(languageOption);
                        break;
                    case "find_unused_assets":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["assetType"] = context.ParseResult.GetValueForOption(assetTypeOption);
                        break;
                    case "lint_xml":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["severityLevel"] = context.ParseResult.GetValueForOption(severityLevelOption);
                        break;
                    case "generate_documentation":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["format"] = context.ParseResult.GetValueForOption(formatOption);
                        break;
                    case "create_compatibility_report":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        break;
                    case "export_definitions":
                        toolArgs["format"] = context.ParseResult.GetValueForOption(formatOption);
                        toolArgs["defType"] = context.ParseResult.GetValueForOption(typeOption);
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["maxDefinitions"] = context.ParseResult.GetValueForOption(maxDefinitionsOption);
                        break;
                    case "analyze_balance":
                        toolArgs["defType"] = context.ParseResult.GetValueForOption(typeOption);
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["focusStat"] = context.ParseResult.GetValueForOption(focusStatOption);
                        break;
                    case "get_recipe_chains":
                        toolArgs["targetDefName"] = context.ParseResult.GetValueForOption(targetDefNameOption);
                        toolArgs["direction"] = context.ParseResult.GetValueForOption(directionOption);
                        toolArgs["maxDepth"] = context.ParseResult.GetValueForOption(maxDepthOption);
                        break;
                    case "find_research_paths":
                        toolArgs["targetResearch"] = context.ParseResult.GetValueForOption(targetResearchOption);
                        toolArgs["includeAllPaths"] = context.ParseResult.GetValueForOption(includeAllPathsOption);
                        break;
                    case "get_biome_compatibility":
                        toolArgs["biomeDefName"] = context.ParseResult.GetValueForOption(biomeDefNameOption);
                        toolArgs["contentType"] = context.ParseResult.GetValueForOption(contentTypeOption);
                        break;
                    case "calculate_room_requirements":
                        toolArgs["targetDefName"] = context.ParseResult.GetValueForOption(targetDefNameOption);
                        toolArgs["includeComfort"] = context.ParseResult.GetValueForOption(includeComfortOption);
                        break;
                    case "get_mod_list":
                    case "get_statistics":
                        // No additional arguments needed
                        break;
                    case "triage_player_log":
                        toolArgs["logPath"] = context.ParseResult.GetValueForOption(logPathOption);
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["maxGroups"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                    case "validate_def_against_runtime":
                        toolArgs["defName"] = context.ParseResult.GetValueForOption(defNameOption);
                        break;
                    case "scan_dlc_dependencies":
                        toolArgs["allowedDlcs"] = context.ParseResult.GetValueForOption(allowedDlcsOption);
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["maxFindingsPerMod"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                    case "audit_scope":
                        toolArgs["scopeType"] = context.ParseResult.GetValueForOption(scopeTypeOption);
                        toolArgs["scopeValue"] = context.ParseResult.GetValueForOption(scopeValueOption);
                        toolArgs["severity"] = context.ParseResult.GetValueForOption(severityOption);
                        toolArgs["maxFindings"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                    case "triage_patch_conflicts":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["severity"] = context.ParseResult.GetValueForOption(severityOption);
                        toolArgs["maxResults"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                    case "content_coverage_report":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["defType"] = context.ParseResult.GetValueForOption(typeOption);
                        toolArgs["maxExamples"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                    case "mod_ready_check":
                        toolArgs["modPackageId"] = context.ParseResult.GetValueForOption(modPackageIdOption);
                        toolArgs["allowedDlcs"] = context.ParseResult.GetValueForOption(allowedDlcsOption);
                        toolArgs["logPath"] = context.ParseResult.GetValueForOption(logPathOption);
                        toolArgs["maxIssues"] = context.ParseResult.GetValueForOption(maxResultsOption);
                        break;
                }

                await RunToolAsync(rimworldPath, modDirsList, modsConfigPath, tool, toolArgs, modConcurrency, 
                    modBatchSize, logLevel, rimworldVersion);
            }
            else
            {
                await RunServerAsync(rimworldPath, modDirsList, serverName, serverVersion, 
                    modConcurrency, modBatchSize, logLevel, rimworldVersion);
            }
        });

        return await rootCommand.InvokeAsync(args);
    }

    static string GetDefaultServerVersion()
    {
        var informationalVersion = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informationalVersion?.Split('+')[0] ?? "0.0.0-local";
    }

    static async Task RunServerAsync(
        string rimworldPath,
        List<string> modDirs,
        string serverName,
        string serverVersion,
        int modConcurrency,
        int modBatchSize,
        string logLevel,
        string rimworldVersion)
    {
        var level = Enum.Parse<LogLevel>(logLevel, true);

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);

        // Configure logging to stderr to avoid polluting MCP stdout
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options =>
        {
            options.LogToStandardErrorThreshold = LogLevel.Trace; // All logs go to stderr
        });
        builder.Logging.SetMinimumLevel(level); // Use the configured log level

        builder.Services.AddSingleton(new ServerData());
        builder.Services.AddSingleton(sp => new ModLoader(
            rimworldPath,
            modBatchSize,
            modDirs,
            sp.GetRequiredService<ILogger<ModLoader>>()));
        builder.Services.AddSingleton<DefParser>();
        builder.Services.AddSingleton<ConflictDetector>();
        builder.Services.AddSingleton(sp => new FileWatcherService(
            sp.GetRequiredService<ILogger<FileWatcherService>>(),
            sp.GetRequiredService<ServerData>(),
            sp.GetRequiredService<ModLoader>(),
            sp.GetRequiredService<DefParser>()));
        builder.Services.AddHostedService<McpServer>();

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var serverData = host.Services.GetRequiredService<ServerData>();
        var modLoader = host.Services.GetRequiredService<ModLoader>();
        var defParser = host.Services.GetRequiredService<DefParser>();
        var conflictDetector = host.Services.GetRequiredService<ConflictDetector>();
        var fileWatcher = host.Services.GetRequiredService<FileWatcherService>();
        
        // Get MCP server to notify when initialization is complete
        var mcpServer = host.Services.GetServices<IHostedService>()
            .OfType<McpServer>()
            .FirstOrDefault();

        try
        {
            logger.LogInformation("{'=', 60}", new string('=', 60));
            logger.LogInformation("{ServerName} v{ServerVersion}", serverName.ToUpper(), serverVersion);
            logger.LogInformation("{'=', 60}", new string('=', 60));

            logger.LogInformation("🚀 Starting MCP server (loading data in background)...");
            logger.LogInformation("🎯 Starting MCP protocol handler...");
            
            // Start background loading immediately without delay
            var backgroundTask = Task.Run(async () =>
            {
                logger.LogInformation("🔄 Background loading started...");
                logger.LogInformation("💡 MCP Server will handle client connections while this loads...");
                await RunBackgroundLoadingAsync(
                    logger, modLoader, defParser, serverData, conflictDetector, 
                    fileWatcher, rimworldPath, modDirs, rimworldVersion, modConcurrency);
                
                // Notify MCP server that initialization is complete
                mcpServer?.NotifyInitializationComplete();
            });
            
            // Run the host - this should block until stdin closes or process is terminated
            logger.LogInformation("🔌 MCP server ready for connections...");
            await host.RunAsync();
            
            // Wait for background task to complete after host shuts down
            logger.LogInformation("⏳ Waiting for background loading to complete...");
            await backgroundTask;
            
            logger.LogInformation("🔚 MCP server has shut down");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to start server");
            Environment.Exit(1);
        }
    }

    private static async Task LoadModsAndDefsAsync(
        ILogger<Program> logger,
        ModLoader modLoader,
        DefParser defParser,
        ServerData serverData,
        int modConcurrency,
        string rimworldVersion)
    {
        logger.LogInformation("📁 Loading mods and scanning defs...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        
        // Load mods first (core and DLCs need to be loaded before mods for load order)
        var modLoadingSw = System.Diagnostics.Stopwatch.StartNew();
        await modLoader.LoadModsAsync(serverData);
        serverData.IsModsLoaded = true;
        modLoadingSw.Stop();
        
        logger.LogInformation("📄 Scanning defs for {ModCount} mods...", serverData.Mods.Count);
        logger.LogInformation("   ⏱️  Mod loading took: {Duration:F2}s", modLoadingSw.Elapsed.TotalSeconds);
        
        // Now scan defs for all mods in parallel
        var defScanningSw = System.Diagnostics.Stopwatch.StartNew();
        using SemaphoreSlim semaphore = new(modConcurrency);
        List<Task> defScanTasks = serverData.Mods.Values.Select(async mod =>
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
        ILogger<Program> logger,
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
            logger.LogInformation("⚙️  CPU Cores: {CpuCores}, Concurrency: Mod={ModConcurrency}, XML={XmlBatch}, ModLoad={ModBatch}", 
                Environment.ProcessorCount, modConcurrency, 16, 16);
            System.Diagnostics.Stopwatch totalStopwatch = System.Diagnostics.Stopwatch.StartNew();
            
            System.Diagnostics.Stopwatch modLoadingStopwatch = System.Diagnostics.Stopwatch.StartNew();
            await LoadModsAndDefsAsync(logger, modLoader, defParser, serverData, modConcurrency, rimworldVersion);
            modLoadingStopwatch.Stop();
            
            // Run conflict detection after loading completes
            logger.LogInformation("🔍 Analyzing mod conflicts...");
            System.Diagnostics.Stopwatch conflictStopwatch = System.Diagnostics.Stopwatch.StartNew();
            conflictDetector.DetectAllConflicts(serverData);
            serverData.IsConflictsAnalyzed = true;
            conflictStopwatch.Stop();
            
            totalStopwatch.Stop();
            
            logger.LogInformation("   ✓ Loaded {ModCount} mods", serverData.Mods.Count);
            logger.LogInformation("   ✓ Found {DefCount} defs", serverData.Defs.Count);

            // Performance timing logs
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

    static async Task RunToolAsync(
        string rimworldPath,
        List<string> modDirs,
        string? modsConfigPath,
        string toolName,
        Dictionary<string, object?> toolArgs,
        int modConcurrency,
        int modBatchSize,
        string logLevel,
        string rimworldVersion)
    {
        var level = Enum.Parse<LogLevel>(logLevel, true);

        var builder = Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Services.AddSingleton(new ServerData());
        builder.Services.AddSingleton(sp => new ModLoader(
            rimworldPath,
            modBatchSize,
            modDirs,
            sp.GetRequiredService<ILogger<ModLoader>>()));
        builder.Services.AddSingleton<DefParser>();
        builder.Services.AddSingleton<ConflictDetector>();

        var host = builder.Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var serverData = host.Services.GetRequiredService<ServerData>();
        var modLoader = host.Services.GetRequiredService<ModLoader>();
        var defParser = host.Services.GetRequiredService<DefParser>();
        var conflictDetector = host.Services.GetRequiredService<ConflictDetector>();

        try
        {
            // Load data silently
            await LoadModsAndDefsAsync(logger, modLoader, defParser, serverData, modConcurrency, rimworldVersion);

            conflictDetector.DetectAllConflicts(serverData);
            serverData.IsConflictsAnalyzed = true;

            // Execute the tool
            string result = toolName switch
            {
                "get_def" => DefinitionTools.GetDef(serverData, (string)toolArgs["defName"]!),
                "get_defs_by_type" => DefinitionTools.GetDefsByType(serverData, (string)toolArgs["type"]!),
                "search_defs" => DefinitionTools.SearchDefs(serverData, 
                    (string)toolArgs["searchTerm"]!, 
                    (string?)toolArgs.GetValueOrDefault("inType"), 
                    (int?)toolArgs.GetValueOrDefault("maxResults") ?? 100),
                "get_mod_list" => ModTools.GetModList(serverData),
                "get_statistics" => StatisticsTools.GetStatistics(serverData),
                "get_conflicts" => ConflictTools.GetConflicts(serverData, conflictDetector,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("conflictType"),
                    (string?)toolArgs.GetValueOrDefault("severity")),
                "calculate_market_value" => MarketValueTools.CalculateMarketValue(serverData, (string)toolArgs["defName"]!),
                "get_references" => ReferenceTools.GetReferences(serverData, 
                    (string)toolArgs["defName"]!, 
                    (bool?)toolArgs.GetValueOrDefault("includeInactive") ?? false),
                "get_def_dependencies" => ValidationTools.GetDefDependencies(serverData, (string)toolArgs["defName"]!),
                "validate_def" => ValidationTools.ValidateDef(serverData, (string)toolArgs["defName"]!),
                "validate_xpath" => ValidationTools.ValidateXPath(serverData, 
                    (string)toolArgs["xpath"]!,
                    (string?)toolArgs.GetValueOrDefault("defName")),
                "get_def_inheritance_tree" => DefinitionTools.GetDefInheritanceTree(serverData, (string)toolArgs["defName"]!),
                "get_patches_for_def" => DefinitionTools.GetPatchesForDef(serverData, (string)toolArgs["defName"]!),
                "compare_defs" => DefinitionTools.CompareDefs(serverData, 
                    (string)toolArgs["defName1"]!, 
                    (string)toolArgs["defName2"]!),
                "get_abstract_defs" => DefinitionTools.GetAbstractDefs(serverData, 
                    (string?)toolArgs.GetValueOrDefault("type")),
                "analyze_mod_compatibility" => ModAnalysisTools.AnalyzeModCompatibility(serverData,
                    (string)toolArgs["modPackageId1"]!,
                    (string)toolArgs["modPackageId2"]!),
                "get_mod_dependencies" => ModAnalysisTools.GetModDependencies(serverData, (string)toolArgs["modPackageId"]!),
                "find_broken_references" => ModAnalysisTools.FindBrokenReferences(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId")),
                "validate_mod_structure" => ModAnalysisTools.ValidateModStructure(serverData, (string)toolArgs["modPackageId"]!),
                "find_duplicate_content" => PerformanceTools.FindDuplicateContent(serverData,
                    (string?)toolArgs.GetValueOrDefault("defType"),
                    (double?)toolArgs.GetValueOrDefault("similarityThreshold") ?? 0.9),
                "suggest_optimizations" => PerformanceTools.SuggestOptimizations(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId")),
                "analyze_texture_usage" => PerformanceTools.AnalyzeTextureUsage(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId")),
                "validate_localization" => DevelopmentTools.ValidateLocalization(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("language")),
                "find_unused_assets" => DevelopmentTools.FindUnusedAssets(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("assetType") ?? "all"),
                "lint_xml" => DevelopmentTools.LintXml(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("severityLevel") ?? "warning"),
                "generate_documentation" => DevelopmentTools.GenerateDocumentation(serverData,
                    (string)toolArgs["modPackageId"]!,
                    (string?)toolArgs.GetValueOrDefault("format") ?? "markdown"),
                "create_compatibility_report" => DevelopmentTools.CreateCompatibilityReport(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId")),
                "export_definitions" => DevelopmentTools.ExportDefinitions(serverData,
                    (string?)toolArgs.GetValueOrDefault("format") ?? "json",
                    (string?)toolArgs.GetValueOrDefault("defType"),
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (int?)toolArgs.GetValueOrDefault("maxDefinitions") ?? 1000),
                "analyze_balance" => GameMechanicsTools.AnalyzeBalance(serverData,
                    (string)toolArgs["defType"]!,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("focusStat")),
                "get_recipe_chains" => GameMechanicsTools.GetRecipeChains(serverData,
                    (string)toolArgs["targetDefName"]!,
                    (string?)toolArgs.GetValueOrDefault("direction") ?? "both",
                    (int?)toolArgs.GetValueOrDefault("maxDepth") ?? 5),
                "find_research_paths" => GameMechanicsTools.FindResearchPaths(serverData,
                    (string)toolArgs["targetResearch"]!,
                    (bool?)toolArgs.GetValueOrDefault("includeAllPaths") ?? false),
                "get_biome_compatibility" => GameMechanicsTools.GetBiomeCompatibility(serverData,
                    (string?)toolArgs.GetValueOrDefault("biomeDefName"),
                    (string?)toolArgs.GetValueOrDefault("contentType") ?? "all"),
                "calculate_room_requirements" => GameMechanicsTools.CalculateRoomRequirements(serverData,
                    (string)toolArgs["targetDefName"]!,
                    (bool?)toolArgs.GetValueOrDefault("includeComfort") ?? true),
                "triage_player_log" => ModWorkflowTools.TriagePlayerLog(serverData,
                    (string)toolArgs["logPath"]!,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (int?)toolArgs.GetValueOrDefault("maxGroups") ?? 15),
                "validate_def_against_runtime" => ModWorkflowTools.ValidateDefAgainstRuntime(serverData,
                    (string)toolArgs["defName"]!),
                "scan_dlc_dependencies" => ModWorkflowTools.ScanDlcDependencies(serverData,
                    (string?)toolArgs.GetValueOrDefault("allowedDlcs") ?? "Core,Biotech",
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (int?)toolArgs.GetValueOrDefault("maxFindingsPerMod") ?? 12),
                "audit_scope" => ModWorkflowTools.AuditScope(serverData,
                    (string)toolArgs["scopeType"]!,
                    (string)toolArgs["scopeValue"]!,
                    (string?)toolArgs.GetValueOrDefault("severity") ?? "warning",
                    (int?)toolArgs.GetValueOrDefault("maxFindings") ?? 40),
                "triage_patch_conflicts" => ModWorkflowTools.TriagePatchConflicts(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("severity") ?? "warning",
                    (int?)toolArgs.GetValueOrDefault("maxResults") ?? 25),
                "content_coverage_report" => ModWorkflowTools.ContentCoverageReport(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("defType"),
                    (int?)toolArgs.GetValueOrDefault("maxExamples") ?? 12),
                "mod_ready_check" => ModWorkflowTools.ModReadyCheck(serverData,
                    (string?)toolArgs.GetValueOrDefault("modPackageId"),
                    (string?)toolArgs.GetValueOrDefault("allowedDlcs") ?? "Core,Biotech",
                    (string?)toolArgs.GetValueOrDefault("logPath"),
                    (int?)toolArgs.GetValueOrDefault("maxIssues") ?? 8),
                _ => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            // Output the result to stdout for the Node.js wrapper to capture
            Console.WriteLine(result);
        }
        catch (Exception ex)
        {
            // Output error as JSON
            var error = new { error = ex.Message };
            Console.WriteLine(JsonSerializer.Serialize(error));
            Environment.Exit(1);
        }
    }
}
