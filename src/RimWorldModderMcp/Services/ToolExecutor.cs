using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using RimWorldModderMcp.Attributes;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Tools.Conflicts;
using RimWorldModderMcp.Tools.Development;
using RimWorldModderMcp.Tools.GameMechanics;
using RimWorldModderMcp.Tools.Patch;
using RimWorldModderMcp.Tools.Performance;
using RimWorldModderMcp.Tools.RimWorld;
using RimWorldModderMcp.Tools.Session;
using RimWorldModderMcp.Tools.Statistics;

namespace RimWorldModderMcp.Services;

public sealed class ToolExecutor
{
    private readonly ILogger<ToolExecutor> _logger;
    private readonly ServerData _serverData;
    private readonly ConflictDetector _conflictDetector;
    private readonly ProjectContext _projectContext;
    private readonly ResultHandleStore _resultHandleStore;
    private readonly Dictionary<string, (MethodInfo method, ParameterInfo[] parameters)> _tools;

    public ToolExecutor(
        ILogger<ToolExecutor> logger,
        ServerData serverData,
        ConflictDetector conflictDetector,
        ProjectContext projectContext,
        ResultHandleStore resultHandleStore)
    {
        _logger = logger;
        _serverData = serverData;
        _conflictDetector = conflictDetector;
        _projectContext = projectContext;
        _resultHandleStore = resultHandleStore;
        _tools = DiscoverTools();
    }

    public IReadOnlyDictionary<string, (MethodInfo method, ParameterInfo[] parameters)> Tools => _tools;

    public ToolExecutionResult Execute(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        ToolExecutionOptions options)
    {
        if (!_tools.TryGetValue(toolName, out var toolInfo))
        {
            throw new ArgumentException($"Unknown tool: {toolName}", nameof(toolName));
        }

        var (method, parameters) = toolInfo;
        var invocationArgs = new List<object?>(parameters.Length);

        foreach (var parameter in parameters)
        {
            if (TryResolveInjectedParameter(parameter, out var injected))
            {
                invocationArgs.Add(injected);
                continue;
            }

            if (arguments.TryGetValue(parameter.Name!, out var suppliedValue))
            {
                invocationArgs.Add(ConvertArgument(suppliedValue, parameter.ParameterType));
                continue;
            }

            if (parameter.HasDefaultValue)
            {
                invocationArgs.Add(parameter.DefaultValue);
                continue;
            }

            throw new ArgumentException($"Missing required parameter: {parameter.Name}");
        }

        object? result;
        try
        {
            result = method.Invoke(null, invocationArgs.ToArray());
        }
        catch (TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw ex.InnerException;
        }

        var rawNode = ToolOutputFormatter.ParseNode(result);
        return ToolOutputFormatter.Format(toolName, rawNode, options, _resultHandleStore);
    }

    private bool TryResolveInjectedParameter(ParameterInfo parameter, out object? value)
    {
        if (parameter.ParameterType == typeof(ServerData))
        {
            value = _serverData;
            return true;
        }

        if (parameter.ParameterType == typeof(ConflictDetector))
        {
            value = _conflictDetector;
            return true;
        }

        if (parameter.ParameterType == typeof(ProjectContext))
        {
            value = _projectContext;
            return true;
        }

        if (parameter.ParameterType == typeof(ResultHandleStore))
        {
            value = _resultHandleStore;
            return true;
        }

        value = null;
        return false;
    }

    private Dictionary<string, (MethodInfo method, ParameterInfo[] parameters)> DiscoverTools()
    {
        var tools = new Dictionary<string, (MethodInfo method, ParameterInfo[] parameters)>(StringComparer.OrdinalIgnoreCase);
        var toolTypes = new[]
        {
            typeof(DefinitionTools),
            typeof(ModTools),
            typeof(ValidationTools),
            typeof(ReferenceTools),
            typeof(MarketValueTools),
            typeof(ConflictTools),
            typeof(StatisticsTools),
            typeof(PatchAnalysisTools),
            typeof(PerformanceTools),
            typeof(DevelopmentTools),
            typeof(GameMechanicsTools),
            typeof(ModAnalysisTools),
            typeof(ModdingAssistanceTools),
            typeof(ModWorkflowTools),
            typeof(SessionTools)
        };

        foreach (var type in toolTypes)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                         .Where(candidate => candidate.GetCustomAttribute<McpServerToolAttribute>() != null))
            {
                var toolName = ConvertMethodNameToToolName(method.Name);
                tools[toolName] = (method, method.GetParameters());

                var legacyToolName = ConvertMethodNameToLegacyToolName(method.Name);
                if (!string.Equals(toolName, legacyToolName, StringComparison.OrdinalIgnoreCase))
                {
                    tools[legacyToolName] = (method, method.GetParameters());
                }
            }
        }

        _logger.LogDebug("Discovered {ToolCount} tools", tools.Count);
        return tools;
    }

    public static string ConvertMethodNameToToolName(string methodName)
    {
        if (string.IsNullOrWhiteSpace(methodName))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(methodName.Length + 8);
        for (var index = 0; index < methodName.Length; index++)
        {
            var current = methodName[index];
            var previous = index > 0 ? methodName[index - 1] : '\0';
            var next = index < methodName.Length - 1 ? methodName[index + 1] : '\0';

            if (index > 0 && char.IsUpper(current) &&
                (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && char.IsLower(next))))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(current));
        }

        return builder
            .ToString()
            .Replace("x_path", "xpath", StringComparison.Ordinal);
    }

    private static string ConvertMethodNameToLegacyToolName(string methodName) =>
        string.Concat(methodName.Select((character, index) =>
            index > 0 && char.IsUpper(character)
                ? "_" + char.ToLowerInvariant(character)
                : char.ToLowerInvariant(character).ToString()));

    public static string GetJsonSchemaType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        if (underlyingType == typeof(string))
        {
            return "string";
        }

        if (underlyingType == typeof(int) || underlyingType == typeof(long) || underlyingType == typeof(short))
        {
            return "integer";
        }

        if (underlyingType == typeof(double) || underlyingType == typeof(float) || underlyingType == typeof(decimal))
        {
            return "number";
        }

        if (underlyingType == typeof(bool))
        {
            return "boolean";
        }

        if (underlyingType.IsArray || (underlyingType.IsGenericType && underlyingType.GetGenericTypeDefinition() == typeof(List<>)))
        {
            return "array";
        }

        return "string";
    }

    private static object? ConvertArgument(object? value, Type targetType)
    {
        if (value == null)
        {
            return null;
        }

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (underlyingType.IsInstanceOfType(value))
        {
            return value;
        }

        try
        {
            if (value is JsonNode node)
            {
                return JsonSerializer.Deserialize(node.ToJsonString(), targetType);
            }

            if (underlyingType == typeof(string))
            {
                return Convert.ToString(value);
            }

            if (underlyingType.IsEnum)
            {
                return Enum.Parse(underlyingType, Convert.ToString(value)!, true);
            }

            if (underlyingType.IsArray && value is IEnumerable<string> stringEnumerable)
            {
                var elementType = underlyingType.GetElementType() ?? typeof(string);
                return stringEnumerable
                    .Select(item => ConvertArgument(item, elementType))
                    .ToArray();
            }

            return Convert.ChangeType(value, underlyingType);
        }
        catch (Exception)
        {
            if (value is string stringValue)
            {
                if (underlyingType == typeof(string[]))
                {
                    return stringValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                }

                return stringValue;
            }

            return value;
        }
    }
}
