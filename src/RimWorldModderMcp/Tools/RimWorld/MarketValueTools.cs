using System.Text.Json;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Attributes;
using System.ComponentModel;

namespace RimWorldModderMcp.Tools.RimWorld;

public static class MarketValueTools
{
    [McpServerTool, Description("Calculate the market value of an item including all modifiers and dependencies.")]
    public static string CalculateMarketValue(
        ServerData serverData,
        [Description("The name of the definition to calculate market value for")] string defName)
    {
        if (serverData == null) return JsonSerializer.Serialize(new { error = "Server not initialized" });
        
        var def = serverData.Defs.GetValueOrDefault(defName);
        if (def == null)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Definition '{defName}' not found",
                defName = defName,
                marketValue = 0
            });
        }

        try
        {
            var baseValue = ExtractBaseMarketValue(def);
            var modifiers = ExtractMarketValueModifiers(def);
            var finalValue = CalculateFinalMarketValue(baseValue, modifiers);
            
            return JsonSerializer.Serialize(new
            {
                defName = defName,
                defType = def.Type,
                calculation = new
                {
                    baseValue = baseValue,
                    modifiers = modifiers.Select(m => new
                    {
                        type = m.Type,
                        value = m.Value,
                        description = m.Description,
                        source = m.Source
                    }).ToList(),
                    finalValue = finalValue
                },
                breakdown = GenerateValueBreakdown(baseValue, modifiers, finalValue),
                relatedItems = FindRelatedValueItems(serverData, def, baseValue)
            });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new
            {
                error = $"Error calculating market value: {ex.Message}",
                defName = defName,
                marketValue = 0
            });
        }
    }

    private static decimal ExtractBaseMarketValue(RimWorldDef def)
    {
        try
        {
            var content = def.Content.ToString();
            
            // Look for marketValue in statBases
            var marketValueMatch = System.Text.RegularExpressions.Regex.Match(content, @"<MarketValue>(\d+(?:\.\d+)?)</MarketValue>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (marketValueMatch.Success && decimal.TryParse(marketValueMatch.Groups[1].Value, out var value))
            {
                return value;
            }
            
            // Look for simple marketValue tag
            marketValueMatch = System.Text.RegularExpressions.Regex.Match(content, @"<marketValue>(\d+(?:\.\d+)?)</marketValue>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (marketValueMatch.Success && decimal.TryParse(marketValueMatch.Groups[1].Value, out value))
            {
                return value;
            }
            
            return 0;
        }
        catch
        {
            return 0;
        }
    }
    
    private static List<ValueModifier> ExtractMarketValueModifiers(RimWorldDef def)
    {
        var modifiers = new List<ValueModifier>();
        var content = def.Content.ToString();
        
        try
        {
            // Check for quality modifiers
            if (content.Contains("<qualityRange>", StringComparison.OrdinalIgnoreCase))
            {
                var qualityMatch = System.Text.RegularExpressions.Regex.Match(content, @"<qualityRange>([^<]+)</qualityRange>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (qualityMatch.Success)
                {
                    modifiers.Add(new ValueModifier
                    {
                        Type = "Quality",
                        Value = 1.5m, // Average quality multiplier
                        Description = $"Quality range affects value: {qualityMatch.Groups[1].Value}",
                        Source = "Definition"
                    });
                }
            }
            
            // Check for stuff categories (material modifiers)
            if (content.Contains("<stuffCategories>", StringComparison.OrdinalIgnoreCase))
            {
                var stuffMatch = System.Text.RegularExpressions.Regex.Match(content, @"<stuffCategories>(.*?)</stuffCategories>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (stuffMatch.Success)
                {
                    var stuffCategories = System.Text.RegularExpressions.Regex.Matches(stuffMatch.Groups[1].Value, @"<li>([^<]+)</li>");
                    modifiers.Add(new ValueModifier
                    {
                        Type = "Material",
                        Value = 1.2m, // Average material multiplier
                        Description = $"Material affects value (categories: {string.Join(", ", stuffCategories.Cast<System.Text.RegularExpressions.Match>().Select(m => m.Groups[1].Value))})",
                        Source = "Definition"
                    });
                }
            }
            
            // Check for cost lists (recipe costs)
            if (content.Contains("<costList>", StringComparison.OrdinalIgnoreCase))
            {
                var costMatch = System.Text.RegularExpressions.Regex.Match(content, @"<costList>(.*?)</costList>", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline);
                if (costMatch.Success)
                {
                    var costs = System.Text.RegularExpressions.Regex.Matches(costMatch.Groups[1].Value, @"<(\w+)>(\d+)</\1>");
                    var totalCost = 0m;
                    var materials = new List<string>();
                    
                    foreach (System.Text.RegularExpressions.Match cost in costs)
                    {
                        if (decimal.TryParse(cost.Groups[2].Value, out var costValue))
                        {
                            totalCost += costValue;
                            materials.Add($"{cost.Groups[1].Value}({costValue})");
                        }
                    }
                    
                    if (totalCost > 0)
                    {
                        modifiers.Add(new ValueModifier
                        {
                            Type = "Recipe Cost",
                            Value = totalCost,
                            Description = $"Materials: {string.Join(", ", materials)} = {totalCost} total",
                            Source = "Recipe"
                        });
                    }
                }
            }
            
            // Check for workAmount (crafting time affects value)
            if (content.Contains("<workAmount>", StringComparison.OrdinalIgnoreCase))
            {
                var workMatch = System.Text.RegularExpressions.Regex.Match(content, @"<workAmount>(\d+(?:\.\d+)?)</workAmount>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (workMatch.Success && decimal.TryParse(workMatch.Groups[1].Value, out var workAmount))
                {
                    modifiers.Add(new ValueModifier
                    {
                        Type = "Work Amount",
                        Value = workAmount / 1000m, // Normalize work amount
                        Description = $"Crafting time: {workAmount} work units",
                        Source = "Recipe"
                    });
                }
            }
        }
        catch (Exception ex)
        {
            modifiers.Add(new ValueModifier
            {
                Type = "Error",
                Value = 0,
                Description = $"Error extracting modifiers: {ex.Message}",
                Source = "System"
            });
        }
        
        return modifiers;
    }
    
    private static decimal CalculateFinalMarketValue(decimal baseValue, List<ValueModifier> modifiers)
    {
        if (baseValue == 0 && modifiers.Count == 0) return 0;
        
        var finalValue = baseValue;
        var hasRecipeCost = false;
        
        foreach (var modifier in modifiers)
        {
            switch (modifier.Type.ToLowerInvariant())
            {
                case "quality":
                    finalValue *= modifier.Value;
                    break;
                case "material":
                    finalValue *= modifier.Value;
                    break;
                case "recipe cost":
                    // If we have recipe cost, use it as base if higher
                    var estimatedValue = modifier.Value * 0.6m; // Materials typically 60% of final value
                    if (estimatedValue > finalValue)
                        finalValue = estimatedValue;
                    hasRecipeCost = true;
                    break;
                case "work amount":
                    // Work amount adds to the value
                    finalValue += modifier.Value;
                    break;
            }
        }
        
        // If no base value but we have recipe cost, estimate based on materials
        if (baseValue == 0 && hasRecipeCost)
        {
            var recipeCostModifier = modifiers.FirstOrDefault(m => m.Type == "Recipe Cost");
            if (recipeCostModifier != null)
            {
                finalValue = Math.Max(finalValue, recipeCostModifier.Value * 0.8m);
            }
        }
        
        return Math.Round(finalValue, 2);
    }
    
    private static object GenerateValueBreakdown(decimal baseValue, List<ValueModifier> modifiers, decimal finalValue)
    {
        var steps = new List<object>();
        var currentValue = baseValue;
        
        if (baseValue > 0)
        {
            steps.Add(new
            {
                step = 1,
                description = "Base market value",
                value = baseValue,
                calculation = $"{baseValue}"
            });
        }
        
        var stepNumber = 2;
        
        foreach (var modifier in modifiers)
        {
            var previousValue = currentValue;
            
            switch (modifier.Type.ToLowerInvariant())
            {
                case "quality":
                    currentValue *= modifier.Value;
                    steps.Add(new
                    {
                        step = stepNumber++,
                        description = modifier.Description,
                        value = currentValue,
                        calculation = $"{previousValue} × {modifier.Value} = {Math.Round(currentValue, 2)}"
                    });
                    break;
                case "material":
                    currentValue *= modifier.Value;
                    steps.Add(new
                    {
                        step = stepNumber++,
                        description = modifier.Description,
                        value = currentValue,
                        calculation = $"{previousValue} × {modifier.Value} = {Math.Round(currentValue, 2)}"
                    });
                    break;
                case "recipe cost":
                    var estimatedValue = modifier.Value * 0.6m;
                    if (estimatedValue > previousValue)
                    {
                        currentValue = estimatedValue;
                        steps.Add(new
                        {
                            step = stepNumber++,
                            description = modifier.Description,
                            value = currentValue,
                            calculation = $"max({previousValue}, {modifier.Value} × 0.6) = {Math.Round(currentValue, 2)}"
                        });
                    }
                    break;
                case "work amount":
                    currentValue += modifier.Value;
                    steps.Add(new
                    {
                        step = stepNumber++,
                        description = modifier.Description,
                        value = currentValue,
                        calculation = $"{previousValue} + {modifier.Value} = {Math.Round(currentValue, 2)}"
                    });
                    break;
            }
        }
        
        return new
        {
            steps = steps,
            finalValue = finalValue
        };
    }
    
    private static List<object> FindRelatedValueItems(ServerData serverData, RimWorldDef def, decimal baseValue)
    {
        var related = new List<object>();
        
        if (baseValue <= 0) return related;
        
        try
        {
            // Find items with similar market values within the same type
            var similarItems = serverData.Defs.Values
                .Where(d => d.Type == def.Type && d.DefName != def.DefName)
                .Select(d => new { def = d, value = ExtractBaseMarketValue(d) })
                .Where(x => x.value > 0)
                .Where(x => Math.Abs(x.value - baseValue) / Math.Max(x.value, baseValue) < 0.5m) // Within 50% of value
                .OrderBy(x => Math.Abs(x.value - baseValue))
                .Take(5)
                .ToList();
                
            foreach (var item in similarItems)
            {
                related.Add(new
                {
                    defName = item.def.DefName,
                    marketValue = item.value,
                    mod = new { packageId = item.def.Mod.PackageId, name = item.def.Mod.Name },
                    valueDifference = Math.Round(item.value - baseValue, 2),
                    percentDifference = Math.Round((item.value - baseValue) / baseValue * 100, 1)
                });
            }
        }
        catch
        {
            // Ignore errors finding related items
        }
        
        return related;
    }
    
    private class ValueModifier
    {
        public string Type { get; set; } = string.Empty;
        public decimal Value { get; set; }
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
    }
}