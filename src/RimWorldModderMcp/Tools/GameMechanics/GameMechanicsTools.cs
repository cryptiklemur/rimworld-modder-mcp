using System.Text.Json;
using System.Xml.Linq;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.GameMechanics;

public static class GameMechanicsTools
{
    [McpServerTool, Description("Use when you want stat comparisons across weapons, items, or other defs.")]
    public static string AnalyzeBalance(
        ServerData serverData,
        [Description("Definition type to analyze (WeaponMelee, WeaponRanged, ThingDef)")] string defType,
        [Description("Optional: specific mod package ID to compare against")] string? modPackageId = null,
        [Description("Stat to focus analysis on (DPS, MarketValue, Mass, etc.)")] string? focusStat = null)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var defsToAnalyze = serverData.Defs.Values
            .Where(d => d.Type.Equals(defType, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!defsToAnalyze.Any())
        {
            return JsonSerializer.Serialize(new { error = $"No definitions found for type '{defType}'" });
        }

        var balanceAnalysis = new List<object>();
        var statComparisons = new Dictionary<string, object>();

        foreach (var def in defsToAnalyze)
        {
            var stats = ExtractStatsFromDefinition(def);
            var analysis = new
            {
                defName = def.DefName,
                mod = new { packageId = def.Mod.PackageId, name = def.Mod.Name },
                stats = stats,
                balanceScore = CalculateBalanceScore(stats, defType),
                outliers = IdentifyStatOutliers(stats, defsToAnalyze, def),
                powerLevel = CalculatePowerLevel(stats, defType)
            };
            
            balanceAnalysis.Add(analysis);
        }

        // Generate stat comparisons
        var allStats = balanceAnalysis.SelectMany(a => ((dynamic)a).stats as Dictionary<string, double> ?? new Dictionary<string, double>())
            .GroupBy(kvp => kvp.Key)
            .ToDictionary(g => g.Key, g => new
            {
                min = g.Min(kvp => kvp.Value),
                max = g.Max(kvp => kvp.Value),
                average = g.Average(kvp => kvp.Value),
                median = CalculateMedian(g.Select(kvp => kvp.Value).ToList()),
                standardDeviation = CalculateStandardDeviation(g.Select(kvp => kvp.Value).ToList())
            });

        var recommendations = GenerateBalanceRecommendations(balanceAnalysis, allStats.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value), focusStat);

        return JsonSerializer.Serialize(new
        {
            defType = defType,
            modFilter = modPackageId,
            focusStat = focusStat,
            totalAnalyzed = balanceAnalysis.Count,
            statComparisons = allStats,
            recommendations = recommendations,
            balanceAnalysis = balanceAnalysis.OrderByDescending(a => ((dynamic)a).powerLevel).Take(50).ToList()
        });
    }

    [McpServerTool, Description("Use when you want ingredient or product crafting chains for one item.")]
    public static string GetRecipeChains(
        ServerData serverData,
        [Description("Target item defName to trace chains for")] string targetDefName,
        [Description("Chain direction (ingredients, products, both)")] string direction = "both",
        [Description("Maximum chain depth")] int maxDepth = 5)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var targetDef = serverData.Defs.GetValueOrDefault(targetDefName);
        if (targetDef == null)
        {
            return JsonSerializer.Serialize(new { error = $"Definition '{targetDefName}' not found" });
        }

        var recipeChains = new List<object>();
        var visitedItems = new HashSet<string>();

        if (direction == "ingredients" || direction == "both")
        {
            var ingredientChains = TraceIngredientChains(serverData, targetDefName, maxDepth, visitedItems);
            recipeChains.AddRange(ingredientChains);
        }

        if (direction == "products" || direction == "both")
        {
            visitedItems.Clear();
            var productChains = TraceProductChains(serverData, targetDefName, maxDepth, visitedItems);
            recipeChains.AddRange(productChains);
        }

        var alternativeRecipes = FindAlternativeRecipes(serverData, targetDefName);
        var criticalBottlenecks = IdentifyCriticalBottlenecks(serverData, recipeChains);

        return JsonSerializer.Serialize(new
        {
            targetItem = targetDefName,
            direction = direction,
            maxDepth = maxDepth,
            totalChains = recipeChains.Count,
            alternativeRecipes = alternativeRecipes,
            criticalBottlenecks = criticalBottlenecks,
            chains = recipeChains
        });
    }

    [McpServerTool, Description("Use when you want the research path needed to unlock a target project.")]
    public static string FindResearchPaths(
        ServerData serverData,
        [Description("Target research defName to find paths to")] string targetResearch,
        [Description("Include all possible paths or just optimal")] bool includeAllPaths = false)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var researchDefs = serverData.Defs.Values
            .Where(d => d.Type.Equals("ResearchProjectDef", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var targetDef = researchDefs.FirstOrDefault(r => r.DefName.Equals(targetResearch, StringComparison.OrdinalIgnoreCase));
        if (targetDef == null)
        {
            return JsonSerializer.Serialize(new { error = $"Research '{targetResearch}' not found" });
        }

        var researchGraph = BuildResearchGraph(researchDefs);
        var paths = FindAllPathsToResearch(researchGraph, targetResearch, includeAllPaths);
        var requiredTechLevels = AnalyzeRequiredTechLevels(serverData, paths);
        var unlockables = FindResearchUnlockables(serverData, targetResearch);

        return JsonSerializer.Serialize(new
        {
            targetResearch = targetResearch,
            includeAllPaths = includeAllPaths,
            totalPaths = paths.Count,
            shortestPath = paths.OrderBy(p => ((List<object>)p).Count).FirstOrDefault(),
            longestPath = paths.OrderByDescending(p => ((List<object>)p).Count).FirstOrDefault(),
            requiredTechLevels = requiredTechLevels,
            unlockables = unlockables,
            allPaths = paths.Take(20).ToList()
        });
    }

    [McpServerTool, Description("Use when you want biome spawn or content compatibility information.")]
    public static string GetBiomeCompatibility(
        ServerData serverData,
        [Description("Optional: specific biome defName to analyze")] string? biomeDefName = null,
        [Description("Content type to check (animals, plants, terrain, all)")] string contentType = "all")
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var biomeAnalysis = new List<object>();

        var biomeDefs = serverData.Defs.Values
            .Where(d => d.Type.Equals("BiomeDef", StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrEmpty(biomeDefName) || d.DefName.Equals(biomeDefName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var biome in biomeDefs)
        {
            var biomeData = AnalyzeBiomeContent(serverData, biome, contentType);
            biomeAnalysis.Add(biomeData);
        }

        var crossBiomeComparisons = GenerateCrossBiomeComparisons(biomeAnalysis, contentType);
        var uniqueSpawns = FindUniqueSpawns(biomeAnalysis);

        return JsonSerializer.Serialize(new
        {
            targetBiome = biomeDefName ?? "all biomes",
            contentType = contentType,
            totalBiomes = biomeAnalysis.Count,
            crossBiomeComparisons = crossBiomeComparisons,
            uniqueSpawns = uniqueSpawns,
            biomeAnalysis = biomeAnalysis
        });
    }

    [McpServerTool, Description("Use when you want building or room requirement details for planning or balancing.")]
    public static string CalculateRoomRequirements(
        ServerData serverData,
        [Description("Building or room defName to analyze")] string targetDefName,
        [Description("Include beauty and comfort calculations")] bool includeComfort = true)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });

        var targetDef = serverData.Defs.GetValueOrDefault(targetDefName);
        if (targetDef == null)
        {
            return JsonSerializer.Serialize(new { error = $"Definition '{targetDefName}' not found" });
        }

        var requirements = AnalyzeRoomRequirements(serverData, targetDef);
        var buildingStats = ExtractBuildingStats(targetDef);
        var roomEffects = CalculateRoomEffects(serverData, targetDef, includeComfort);
        var constructionCosts = CalculateConstructionCosts(serverData, targetDef);
        var maintenanceNeeds = AnalyzeMaintenanceRequirements(targetDef);

        return JsonSerializer.Serialize(new
        {
            targetBuilding = targetDefName,
            buildingType = targetDef.Type,
            includeComfort = includeComfort,
            spaceRequirements = requirements.spaceRequirements,
            powerRequirements = requirements.powerRequirements,
            materialRequirements = constructionCosts,
            roomEffects = roomEffects,
            maintenanceNeeds = maintenanceNeeds,
            buildingStats = buildingStats,
            recommendations = GenerateRoomRecommendations(requirements, roomEffects, targetDef)
        });
    }

    private static Dictionary<string, double> ExtractStatsFromDefinition(RimWorldDef def)
    {
        var stats = new Dictionary<string, double>();
        
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            
            // Extract stat bases
            var statBases = doc.Descendants("statBases").FirstOrDefault();
            if (statBases != null)
            {
                foreach (var stat in statBases.Elements())
                {
                    if (double.TryParse(stat.Value, out var value))
                    {
                        stats[stat.Name.LocalName] = value;
                    }
                }
            }
            
            // Extract weapon-specific stats
            if (def.Type.Contains("Weapon"))
            {
                var verbs = doc.Descendants("verbs").Descendants("li").FirstOrDefault();
                if (verbs != null)
                {
                    ExtractVerbStats(verbs, stats);
                }
                
                var tools = doc.Descendants("tools").Descendants("li");
                foreach (var tool in tools)
                {
                    ExtractToolStats(tool, stats);
                }
            }
            
            // Extract common properties
            ExtractCommonProperties(doc.Root, stats);
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return stats;
    }

    private static void ExtractVerbStats(XElement verb, Dictionary<string, double> stats)
    {
        var verbStats = new Dictionary<string, string>
        {
            { "verbClass", "verbClass" },
            { "range", "range" },
            { "warmupTime", "warmupTime" },
            { "defaultCooldownTime", "cooldown" },
            { "burstShotCount", "burstCount" },
            { "ticksBetweenBurstShots", "burstDelay" }
        };
        
        foreach (var kvp in verbStats)
        {
            var element = verb.Element(kvp.Key);
            if (element != null && double.TryParse(element.Value, out var value))
            {
                stats[kvp.Value] = value;
            }
        }
    }

    private static void ExtractToolStats(XElement tool, Dictionary<string, double> stats)
    {
        var power = tool.Element("power");
        var cooldown = tool.Element("cooldownTime");
        var label = tool.Element("label")?.Value ?? "melee";
        
        if (power != null && double.TryParse(power.Value, out var powerValue))
        {
            stats[$"{label}_power"] = powerValue;
        }
        
        if (cooldown != null && double.TryParse(cooldown.Value, out var cooldownValue))
        {
            stats[$"{label}_cooldown"] = cooldownValue;
        }
    }

    private static void ExtractCommonProperties(XElement? doc, Dictionary<string, double> stats)
    {
        if (doc == null) return;
        
        var properties = new Dictionary<string, string>
        {
            { "mass", "mass" },
            { "marketValue", "marketValue" },
            { "workToMake", "workToMake" },
            { "maxHitPoints", "hitPoints" }
        };
        
        foreach (var kvp in properties)
        {
            var element = doc.Descendants(kvp.Key).FirstOrDefault();
            if (element != null && double.TryParse(element.Value, out var value))
            {
                stats[kvp.Value] = value;
            }
        }
    }

    private static double CalculateBalanceScore(Dictionary<string, double> stats, string defType)
    {
        // Simple balance scoring based on key stats
        if (defType.Contains("Weapon"))
        {
            var dps = CalculateDPS(stats);
            var marketValue = stats.GetValueOrDefault("marketValue", 100);
            return dps / Math.Max(marketValue / 100, 1); // DPS per 100 silver
        }
        
        return 1.0; // Default neutral score
    }

    private static double CalculateDPS(Dictionary<string, double> stats)
    {
        var damage = stats.GetValueOrDefault("damage", 0);
        var cooldown = stats.GetValueOrDefault("cooldown", 1);
        var burstCount = stats.GetValueOrDefault("burstCount", 1);
        
        if (cooldown <= 0) cooldown = 1;
        
        return (damage * burstCount) / cooldown;
    }

    private static List<string> IdentifyStatOutliers(Dictionary<string, double> stats, List<RimWorldDef> allDefs, RimWorldDef currentDef)
    {
        var outliers = new List<string>();
        
        foreach (var stat in stats)
        {
            var allValues = allDefs.SelectMany(d => ExtractStatsFromDefinition(d))
                .Where(kvp => kvp.Key == stat.Key)
                .Select(kvp => kvp.Value)
                .ToList();
            
            if (allValues.Count > 2)
            {
                var mean = allValues.Average();
                var stdDev = CalculateStandardDeviation(allValues);
                
                if (Math.Abs(stat.Value - mean) > 2 * stdDev)
                {
                    outliers.Add($"{stat.Key}: {stat.Value:F2} (avg: {mean:F2})");
                }
            }
        }
        
        return outliers;
    }

    private static double CalculatePowerLevel(Dictionary<string, double> stats, string defType)
    {
        if (defType.Contains("Weapon"))
        {
            return CalculateDPS(stats) * stats.GetValueOrDefault("range", 1);
        }
        
        return stats.GetValueOrDefault("marketValue", 0);
    }

    private static double CalculateMedian(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        var mid = sorted.Count / 2;
        
        if (sorted.Count % 2 == 0)
            return (sorted[mid - 1] + sorted[mid]) / 2.0;
        
        return sorted[mid];
    }

    private static double CalculateStandardDeviation(List<double> values)
    {
        if (values.Count <= 1) return 0;
        
        var mean = values.Average();
        var sumOfSquares = values.Sum(x => Math.Pow(x - mean, 2));
        return Math.Sqrt(sumOfSquares / (values.Count - 1));
    }

    private static List<string> GenerateBalanceRecommendations(List<object> analysis, Dictionary<string, object> statComparisons, string? focusStat)
    {
        var recommendations = new List<string>();
        
        var highPowerItems = analysis.Count(a => ((dynamic)a).powerLevel > analysis.Average(x => (double)((dynamic)x).powerLevel) * 1.5);
        if (highPowerItems > 0)
        {
            recommendations.Add($"{highPowerItems} items may be overpowered compared to others");
        }
        
        if (!string.IsNullOrEmpty(focusStat) && statComparisons.ContainsKey(focusStat))
        {
            var focusStats = (dynamic)statComparisons[focusStat];
            if (focusStats.max > focusStats.average * 3)
            {
                recommendations.Add($"{focusStat} shows high variance - review extreme values");
            }
        }
        
        recommendations.Add("Consider weapon/item progression curves");
        recommendations.Add("Test items in actual gameplay scenarios");
        
        return recommendations;
    }

    private static List<object> TraceIngredientChains(ServerData serverData, string targetDefName, int maxDepth, HashSet<string> visited)
    {
        var chains = new List<object>();
        
        if (maxDepth <= 0 || visited.Contains(targetDefName)) return chains;
        visited.Add(targetDefName);
        
        var recipes = serverData.Defs.Values
            .Where(d => d.Type.Equals("RecipeDef", StringComparison.OrdinalIgnoreCase))
            .Where(r => RecipeProduces(r, targetDefName))
            .ToList();
        
        foreach (var recipe in recipes)
        {
            var ingredients = ExtractRecipeIngredients(recipe);
            var chain = new
            {
                recipe = recipe.DefName,
                target = targetDefName,
                ingredients = ingredients,
                depth = maxDepth,
                subChains = ingredients.SelectMany<dynamic, object>(ing => TraceIngredientChains(serverData, ing.defName, maxDepth - 1, new HashSet<string>(visited))).ToList()
            };
            chains.Add(chain);
        }
        
        return chains;
    }

    private static List<object> TraceProductChains(ServerData serverData, string targetDefName, int maxDepth, HashSet<string> visited)
    {
        var chains = new List<object>();
        
        if (maxDepth <= 0 || visited.Contains(targetDefName)) return chains;
        visited.Add(targetDefName);
        
        var recipes = serverData.Defs.Values
            .Where(d => d.Type.Equals("RecipeDef", StringComparison.OrdinalIgnoreCase))
            .Where(r => RecipeUsesIngredient(r, targetDefName))
            .ToList();
        
        foreach (var recipe in recipes)
        {
            var products = ExtractRecipeProducts(recipe);
            var chain = new
            {
                recipe = recipe.DefName,
                source = targetDefName,
                products = products,
                depth = maxDepth,
                subChains = products.SelectMany<dynamic, object>(prod => TraceProductChains(serverData, prod.defName, maxDepth - 1, new HashSet<string>(visited))).ToList()
            };
            chains.Add(chain);
        }
        
        return chains;
    }

    private static bool RecipeProduces(RimWorldDef recipe, string targetDefName)
    {
        try
        {
            var doc = XDocument.Parse(recipe.Content.ToString());
            var products = doc.Descendants("products").Descendants("li");
            return products.Any(p => p.Element("defName")?.Value == targetDefName);
        }
        catch
        {
            return false;
        }
    }

    private static bool RecipeUsesIngredient(RimWorldDef recipe, string ingredientDefName)
    {
        try
        {
            var doc = XDocument.Parse(recipe.Content.ToString());
            var ingredients = doc.Descendants("ingredients").Descendants("li");
            return ingredients.Any(i => i.Element("filter")?.Descendants("thingDefs")?.Descendants("li")?.Any(td => td.Value == ingredientDefName) == true);
        }
        catch
        {
            return false;
        }
    }

    private static List<dynamic> ExtractRecipeIngredients(RimWorldDef recipe)
    {
        var ingredients = new List<dynamic>();
        
        try
        {
            var doc = XDocument.Parse(recipe.Content.ToString());
            var ingredientElements = doc.Descendants("ingredients").Descendants("li");
            
            foreach (var ingredient in ingredientElements)
            {
                var filter = ingredient.Element("filter");
                var count = ingredient.Element("count");
                
                if (filter != null)
                {
                    var thingDefs = filter.Descendants("thingDefs").Descendants("li");
                    foreach (var thingDef in thingDefs)
                    {
                        ingredients.Add(new
                        {
                            defName = thingDef.Value,
                            count = int.TryParse(count?.Value, out var c) ? c : 1
                        });
                    }
                }
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return ingredients;
    }

    private static List<dynamic> ExtractRecipeProducts(RimWorldDef recipe)
    {
        var products = new List<dynamic>();
        
        try
        {
            var doc = XDocument.Parse(recipe.Content.ToString());
            var productElements = doc.Descendants("products").Descendants("li");
            
            foreach (var product in productElements)
            {
                var defName = product.Element("defName");
                var count = product.Element("count");
                
                if (defName != null)
                {
                    products.Add(new
                    {
                        defName = defName.Value,
                        count = int.TryParse(count?.Value, out var c) ? c : 1
                    });
                }
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return products;
    }

    private static List<object> FindAlternativeRecipes(ServerData serverData, string targetDefName)
    {
        return serverData.Defs.Values
            .Where(d => d.Type.Equals("RecipeDef", StringComparison.OrdinalIgnoreCase))
            .Where(r => RecipeProduces(r, targetDefName))
            .Select(r => new { recipeName = r.DefName, mod = r.Mod.Name })
            .Cast<object>()
            .ToList();
    }

    private static List<string> IdentifyCriticalBottlenecks(ServerData serverData, List<object> chains)
    {
        // Identify ingredients that appear in multiple chains
        var ingredientFrequency = new Dictionary<string, int>();
        
        foreach (var chain in chains)
        {
            var ingredients = ((dynamic)chain).ingredients as List<dynamic> ?? new List<dynamic>();
            foreach (var ingredient in ingredients)
            {
                var defName = ingredient.defName as string ?? "";
                ingredientFrequency[defName] = ingredientFrequency.GetValueOrDefault(defName, 0) + 1;
            }
        }
        
        return ingredientFrequency
            .Where(kvp => kvp.Value > 2)
            .OrderByDescending(kvp => kvp.Value)
            .Take(10)
            .Select(kvp => $"{kvp.Key} (used in {kvp.Value} recipes)")
            .ToList();
    }

    private static Dictionary<string, List<object>> BuildResearchGraph(List<RimWorldDef> researchDefs)
    {
        var graph = new Dictionary<string, List<object>>();
        
        foreach (var research in researchDefs)
        {
            var prerequisites = ExtractResearchPrerequisites(research);
            graph[research.DefName] = prerequisites;
        }
        
        return graph;
    }

    private static List<object> ExtractResearchPrerequisites(RimWorldDef research)
    {
        var prerequisites = new List<object>();
        
        try
        {
            var doc = XDocument.Parse(research.Content.ToString());
            var prereqs = doc.Descendants("prerequisites").Descendants("li");
            
            foreach (var prereq in prereqs)
            {
                prerequisites.Add(new
                {
                    defName = prereq.Value,
                    type = "research"
                });
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return prerequisites;
    }

    private static List<List<object>> FindAllPathsToResearch(Dictionary<string, List<object>> graph, string targetResearch, bool includeAllPaths)
    {
        var paths = new List<List<object>>();
        var visited = new HashSet<string>();
        var currentPath = new List<object>();
        
        FindResearchPathsRecursive(graph, targetResearch, currentPath, paths, visited, includeAllPaths);
        
        return paths;
    }

    private static void FindResearchPathsRecursive(Dictionary<string, List<object>> graph, string current, List<object> currentPath, List<List<object>> allPaths, HashSet<string> visited, bool includeAllPaths)
    {
        if (visited.Contains(current)) return;
        
        visited.Add(current);
        currentPath.Add(new { research = current, depth = currentPath.Count });
        
        if (graph.ContainsKey(current) && graph[current].Any())
        {
            foreach (var prereq in graph[current])
            {
                var prereqName = ((dynamic)prereq).defName as string ?? "";
                FindResearchPathsRecursive(graph, prereqName, new List<object>(currentPath), allPaths, new HashSet<string>(visited), includeAllPaths);
            }
        }
        else
        {
            // Found a complete path
            allPaths.Add(new List<object>(currentPath));
            if (!includeAllPaths && allPaths.Count >= 5) return; // Limit paths if not including all
        }
    }

    private static object AnalyzeRequiredTechLevels(ServerData serverData, List<List<object>> paths)
    {
        var techLevels = new Dictionary<string, int>();
        
        foreach (var path in paths)
        {
            foreach (var step in path)
            {
                var researchName = ((dynamic)step).research as string ?? "";
                var research = serverData.Defs.GetValueOrDefault(researchName);
                if (research != null)
                {
                    var techLevel = ExtractTechLevel(research);
                    techLevels[techLevel] = techLevels.GetValueOrDefault(techLevel, 0) + 1;
                }
            }
        }
        
        return new
        {
            requiredTechLevels = techLevels,
            highestTechLevel = techLevels.Keys.OrderBy(k => k).LastOrDefault() ?? "Neolithic"
        };
    }

    private static string ExtractTechLevel(RimWorldDef research)
    {
        try
        {
            var doc = XDocument.Parse(research.Content.ToString());
            var techLevel = doc.Descendants("techLevel").FirstOrDefault()?.Value;
            return techLevel ?? "Industrial";
        }
        catch
        {
            return "Industrial";
        }
    }

    private static List<object> FindResearchUnlockables(ServerData serverData, string researchDefName)
    {
        var unlockables = new List<object>();
        
        // Find recipes that require this research
        var recipes = serverData.Defs.Values
            .Where(d => d.Type.Equals("RecipeDef", StringComparison.OrdinalIgnoreCase))
            .Where(r => RequiresResearch(r, researchDefName));
        
        unlockables.AddRange(recipes.Select(r => new { type = "recipe", defName = r.DefName }));
        
        // Find buildings that require this research
        var buildings = serverData.Defs.Values
            .Where(d => d.Type.Equals("ThingDef", StringComparison.OrdinalIgnoreCase))
            .Where(b => RequiresResearch(b, researchDefName));
        
        unlockables.AddRange(buildings.Select(b => new { type = "building", defName = b.DefName }));
        
        return unlockables;
    }

    private static bool RequiresResearch(RimWorldDef def, string researchDefName)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var researchPrereqs = doc.Descendants("researchPrerequisites").Descendants("li");
            return researchPrereqs.Any(r => r.Value == researchDefName);
        }
        catch
        {
            return false;
        }
    }

    private static object AnalyzeBiomeContent(ServerData serverData, RimWorldDef biome, string contentType)
    {
        var animals = new List<object>();
        var plants = new List<object>();
        var terrain = new List<object>();
        
        try
        {
            var doc = XDocument.Parse(biome.Content.ToString());
            
            if (contentType == "animals" || contentType == "all")
            {
                animals = ExtractBiomeAnimals(doc.Root).ToList();
            }
            
            if (contentType == "plants" || contentType == "all")
            {
                plants = ExtractBiomePlants(doc.Root).ToList();
            }
            
            if (contentType == "terrain" || contentType == "all")
            {
                terrain = ExtractBiomeTerrain(doc.Root).ToList();
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return new
        {
            biome = new { defName = biome.DefName, mod = biome.Mod.Name },
            animals = animals,
            plants = plants,
            terrain = terrain,
            totalSpawns = animals.Count + plants.Count + terrain.Count,
            biodiversityScore = CalculateBiodiversityScore(animals, plants)
        };
    }

    private static IEnumerable<object> ExtractBiomeAnimals(XElement? doc)
    {
        if (doc == null) return new List<object>();
        
        var wildAnimals = doc.Descendants("wildAnimals").Descendants("li");
        return wildAnimals.Select(animal => new
        {
            defName = animal.Element("animal")?.Value ?? "",
            commonality = double.TryParse(animal.Element("commonality")?.Value, out var c) ? c : 0.0
        });
    }

    private static IEnumerable<object> ExtractBiomePlants(XElement? doc)
    {
        if (doc == null) return new List<object>();
        
        var wildPlants = doc.Descendants("wildPlants").Descendants("li");
        return wildPlants.Select(plant => new
        {
            defName = plant.Element("plant")?.Value ?? "",
            commonality = double.TryParse(plant.Element("commonality")?.Value, out var c) ? c : 0.0
        });
    }

    private static IEnumerable<object> ExtractBiomeTerrain(XElement? doc)
    {
        if (doc == null) return new List<object>();
        
        var terrainTypes = doc.Descendants("terrainsByFertility").Descendants("li");
        return terrainTypes.Select(terrain => new
        {
            defName = terrain.Element("terrain")?.Value ?? "",
            fertility = double.TryParse(terrain.Element("fertility")?.Value, out var f) ? f : 0.0
        });
    }

    private static double CalculateBiodiversityScore(List<object> animals, List<object> plants)
    {
        // Simple biodiversity calculation based on species count and distribution
        var totalSpecies = animals.Count + plants.Count;
        if (totalSpecies == 0) return 0;
        
        var totalCommonality = animals.Sum(a => (double)((dynamic)a).commonality) + 
                              plants.Sum(p => (double)((dynamic)p).commonality);
        
        return totalSpecies * (totalCommonality / totalSpecies); // Species count weighted by average commonality
    }

    private static object GenerateCrossBiomeComparisons(List<object> biomeAnalysis, string contentType)
    {
        var commonSpecies = new Dictionary<string, int>();
        var uniqueSpecies = new Dictionary<string, List<string>>();
        
        foreach (var biome in biomeAnalysis)
        {
            var biomeName = ((dynamic)biome).biome.defName as string ?? "";
            var species = new HashSet<string>();
            
            if (contentType == "animals" || contentType == "all")
            {
                var animals = ((dynamic)biome).animals as List<object> ?? new List<object>();
                foreach (var animal in animals)
                {
                    var defName = ((dynamic)animal).defName as string ?? "";
                    species.Add(defName);
                    commonSpecies[defName] = commonSpecies.GetValueOrDefault(defName, 0) + 1;
                }
            }
            
            if (contentType == "plants" || contentType == "all")
            {
                var plants = ((dynamic)biome).plants as List<object> ?? new List<object>();
                foreach (var plant in plants)
                {
                    var defName = ((dynamic)plant).defName as string ?? "";
                    species.Add(defName);
                    commonSpecies[defName] = commonSpecies.GetValueOrDefault(defName, 0) + 1;
                }
            }
            
            uniqueSpecies[biomeName] = species.ToList();
        }
        
        return new
        {
            mostCommonSpecies = commonSpecies.OrderByDescending(kvp => kvp.Value).Take(10).ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            biomeUniqueSpecies = uniqueSpecies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count)
        };
    }

    private static List<object> FindUniqueSpawns(List<object> biomeAnalysis)
    {
        var speciesBiomes = new Dictionary<string, List<string>>();
        
        foreach (var biome in biomeAnalysis)
        {
            var biomeName = ((dynamic)biome).biome.defName as string ?? "";
            var allSpecies = new List<string>();
            
            var animals = ((dynamic)biome).animals as List<object> ?? new List<object>();
            allSpecies.AddRange(animals.Select(a => ((dynamic)a).defName as string ?? ""));
            
            var plants = ((dynamic)biome).plants as List<object> ?? new List<object>();
            allSpecies.AddRange(plants.Select(p => ((dynamic)p).defName as string ?? ""));
            
            foreach (var species in allSpecies)
            {
                if (!speciesBiomes.ContainsKey(species))
                    speciesBiomes[species] = new List<string>();
                speciesBiomes[species].Add(biomeName);
            }
        }
        
        return speciesBiomes
            .Where(kvp => kvp.Value.Count == 1)
            .Select(kvp => new { species = kvp.Key, exclusiveTo = kvp.Value.First() })
            .Cast<object>()
            .ToList();
    }

    private static dynamic AnalyzeRoomRequirements(ServerData serverData, RimWorldDef targetDef)
    {
        var spaceRequirements = ExtractSpaceRequirements(targetDef);
        var powerRequirements = ExtractPowerRequirements(targetDef);
        
        return new
        {
            spaceRequirements = spaceRequirements,
            powerRequirements = powerRequirements
        };
    }

    private static object ExtractSpaceRequirements(RimWorldDef def)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var size = doc.Descendants("size").FirstOrDefault();
            var minRoomSize = doc.Descendants("minRoomSize").FirstOrDefault();
            
            return new
            {
                buildingSize = size?.Value ?? "1,1",
                minRoomSize = minRoomSize?.Value ?? "Not specified",
                blocksMovement = doc.Descendants("passability").FirstOrDefault()?.Value == "Impassable"
            };
        }
        catch
        {
            return new { buildingSize = "1,1", minRoomSize = "Not specified", blocksMovement = false };
        }
    }

    private static object ExtractPowerRequirements(RimWorldDef def)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var powerConsumption = doc.Descendants("basePowerConsumption").FirstOrDefault();
            var powerOutput = doc.Descendants("basePowerConsumption").FirstOrDefault(); // Negative for generators
            
            return new
            {
                powerConsumption = double.TryParse(powerConsumption?.Value, out var consumption) ? consumption : 0,
                isPowerProducer = consumption < 0,
                requiresPower = consumption > 0
            };
        }
        catch
        {
            return new { powerConsumption = 0.0, isPowerProducer = false, requiresPower = false };
        }
    }

    private static Dictionary<string, double> ExtractBuildingStats(RimWorldDef def)
    {
        return ExtractStatsFromDefinition(def);
    }

    private static object CalculateRoomEffects(ServerData serverData, RimWorldDef def, bool includeComfort)
    {
        var effects = new Dictionary<string, double>();
        
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            
            // Extract beauty
            var beauty = doc.Descendants("beauty").FirstOrDefault();
            if (beauty != null && double.TryParse(beauty.Value, out var beautyValue))
            {
                effects["beauty"] = beautyValue;
            }
            
            if (includeComfort)
            {
                // Extract comfort effects
                var comfort = doc.Descendants("comfort").FirstOrDefault();
                if (comfort != null && double.TryParse(comfort.Value, out var comfortValue))
                {
                    effects["comfort"] = comfortValue;
                }
            }
            
            // Extract other room stats
            var cleanliness = doc.Descendants("cleanliness").FirstOrDefault();
            if (cleanliness != null && double.TryParse(cleanliness.Value, out var cleanValue))
            {
                effects["cleanliness"] = cleanValue;
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return new
        {
            beautyEffect = effects.GetValueOrDefault("beauty", 0),
            comfortEffect = effects.GetValueOrDefault("comfort", 0),
            cleanlinessEffect = effects.GetValueOrDefault("cleanliness", 0),
            overallRoomImpact = effects.Values.Sum()
        };
    }

    private static object CalculateConstructionCosts(ServerData serverData, RimWorldDef def)
    {
        var costs = new List<object>();
        
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var costList = doc.Descendants("costList").Descendants();
            
            foreach (var cost in costList)
            {
                if (int.TryParse(cost.Value, out var amount))
                {
                    costs.Add(new
                    {
                        material = cost.Name.LocalName,
                        amount = amount
                    });
                }
            }
        }
        catch
        {
            // Handle XML parsing errors
        }
        
        return new
        {
            materials = costs,
            totalMaterials = costs.Count,
            estimatedWork = ExtractWorkAmount(def)
        };
    }

    private static double ExtractWorkAmount(RimWorldDef def)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var workToBuild = doc.Descendants("workToBuild").FirstOrDefault();
            return double.TryParse(workToBuild?.Value, out var work) ? work : 100;
        }
        catch
        {
            return 100;
        }
    }

    private static object AnalyzeMaintenanceRequirements(RimWorldDef def)
    {
        try
        {
            var doc = XDocument.Parse(def.Content.ToString());
            var deterioration = doc.Descendants("deteriorateFromEnvironmentalEffects").FirstOrDefault()?.Value == "true";
            var flammable = doc.Descendants("flammability").FirstOrDefault()?.Value != "NonFlammable";
            
            return new
            {
                requiresMaintenance = deterioration,
                environmentalDamage = deterioration,
                flammable = flammable,
                maintenanceLevel = deterioration ? "High" : "Low"
            };
        }
        catch
        {
            return new
            {
                requiresMaintenance = false,
                environmentalDamage = false,
                flammable = true,
                maintenanceLevel = "Unknown"
            };
        }
    }

    private static List<string> GenerateRoomRecommendations(dynamic requirements, object roomEffects, RimWorldDef def)
    {
        var recommendations = new List<string>();
        
        var powerReqs = requirements.powerRequirements;
        if (powerReqs.requiresPower)
        {
            recommendations.Add($"Requires {powerReqs.powerConsumption}W of power");
        }
        
        var effects = (dynamic)roomEffects;
        if (effects.beautyEffect > 0)
        {
            recommendations.Add("Improves room beauty - place in important rooms");
        }
        else if (effects.beautyEffect < 0)
        {
            recommendations.Add("Reduces room beauty - consider placement carefully");
        }
        
        if (effects.comfortEffect > 0)
        {
            recommendations.Add("Provides comfort - useful in recreation areas");
        }
        
        recommendations.Add("Test room layout for optimal efficiency");
        
        return recommendations;
    }
}
