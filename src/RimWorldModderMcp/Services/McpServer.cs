using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModderMcp.Attributes;
using RimWorldModderMcp.Models;
using RimWorldModderMcp.Services;
using RimWorldModderMcp.Tools.RimWorld;
using RimWorldModderMcp.Tools.Conflicts;
using RimWorldModderMcp.Tools.Statistics;
using RimWorldModderMcp.Tools.Patch;
using RimWorldModderMcp.Tools.Performance;
using RimWorldModderMcp.Tools.Development;
using RimWorldModderMcp.Tools.GameMechanics;

namespace RimWorldModderMcp.Services;

public class McpServer : BackgroundService
{
    private readonly ILogger<McpServer> _logger;
    private readonly ServerData _serverData;
    private readonly ConflictDetector _conflictDetector;
    private readonly Dictionary<string, (MethodInfo method, Type declaringType, ParameterInfo[] parameters)> _tools;
    private readonly SemaphoreSlim _initializationSemaphore = new(0, 1);

    public McpServer(ILogger<McpServer> logger, ServerData serverData, ConflictDetector conflictDetector)
    {
        _logger = logger;
        _serverData = serverData;
        _conflictDetector = conflictDetector;
        _tools = DiscoverTools();
    }

    public void NotifyInitializationComplete()
    {
        _initializationSemaphore.Release();
        _logger.LogInformation("🎯 MCP Server data initialization complete - ready for tool calls");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogDebug("🔌 MCP Server starting protocol handler...");
        
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin);
            using var stdout = Console.OpenStandardOutput();
            using var writer = new StreamWriter(stdout) { AutoFlush = true };

            string? line;
            while (!stoppingToken.IsCancellationRequested && (line = await reader.ReadLineAsync()) != null)
            {
                try
                {
                    await HandleMcpMessage(line, writer, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error handling MCP message: {Message}", line);
                    await SendErrorResponse(writer, null, -32603, "Internal error", ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MCP Server crashed");
            throw;
        }
    }

    private async Task HandleMcpMessage(string message, StreamWriter writer, CancellationToken cancellationToken)
    {
        _logger.LogDebug("📨 Received MCP message: {Message}", message.Length > 200 ? message[..200] + "..." : message);

        JsonNode? jsonNode;
        try
        {
            jsonNode = JsonNode.Parse(message);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning("Invalid JSON received: {Error}", ex.Message);
            await SendErrorResponse(writer, null, -32700, "Parse error", "Invalid JSON");
            return;
        }

        if (jsonNode == null)
        {
            await SendErrorResponse(writer, null, -32600, "Invalid Request", "Null JSON");
            return;
        }

        var id = jsonNode["id"]?.GetValue<int?>();
        var method = jsonNode["method"]?.GetValue<string>();
        var parameters = jsonNode["params"]?.AsObject();

        if (string.IsNullOrEmpty(method))
        {
            await SendErrorResponse(writer, id, -32600, "Invalid Request", "Missing method");
            return;
        }

        _logger.LogDebug("🎯 Handling method: {Method} with ID: {Id}", method, id);

        try
        {
            switch (method)
            {
                case "initialize":
                    await HandleInitialize(writer, id, parameters);
                    break;

                case "tools/list":
                    await HandleToolsList(writer, id);
                    break;

                case "tools/call":
                    await HandleToolsCall(writer, id, parameters, cancellationToken);
                    break;

                case "notifications/initialized":
                    // Client indicates it's ready - no response needed
                    _logger.LogDebug("✅ Client initialized notification received");
                    break;

                default:
                    await SendErrorResponse(writer, id, -32601, "Method not found", $"Unknown method: {method}");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling method {Method}", method);
            await SendErrorResponse(writer, id, -32603, "Internal error", ex.Message);
        }
    }

    private async Task HandleInitialize(StreamWriter writer, int? id, JsonObject? parameters)
    {
        _logger.LogInformation("🚀 MCP Client initializing...");
        
        var protocolVersion = parameters?["protocolVersion"]?.GetValue<string>() ?? "unknown";
        var clientInfo = parameters?["clientInfo"]?.AsObject();
        var clientName = clientInfo?["name"]?.GetValue<string>() ?? "unknown";
        var clientVersion = clientInfo?["version"]?.GetValue<string>() ?? "unknown";

        _logger.LogInformation("📱 Client: {ClientName} v{ClientVersion}, Protocol: {Protocol}", 
            clientName, clientVersion, protocolVersion);

        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            result = new
            {
                protocolVersion = "2024-11-05",
                capabilities = new
                {
                    tools = new { }
                },
                serverInfo = new
                {
                    name = "rimworld-modder-mcp",
                    version = GetServerVersion()
                }
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await writer.WriteLineAsync(json);
        _logger.LogDebug("📤 Sent initialize response");
    }

    private async Task HandleToolsList(StreamWriter writer, int? id)
    {
        _logger.LogDebug("📋 Listing {ToolCount} available tools", _tools.Count);

        var tools = _tools.Select(kvp =>
        {
            var toolName = kvp.Key;
            var (method, _, parameters) = kvp.Value;
            
            // Get description from attribute
            var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? 
                             $"Execute {toolName} tool";

            // Build input schema from method parameters
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var param in parameters)
            {
                if (param.Name == "serverData") continue; // Skip injected parameter

                var paramDescription = param.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? 
                                     $"{param.Name} parameter";

                var isRequired = !param.HasDefaultValue && param.ParameterType.IsValueType && 
                                Nullable.GetUnderlyingType(param.ParameterType) == null;

                if (isRequired && param.ParameterType == typeof(string))
                {
                    // String parameters without default values are required
                    required.Add(param.Name);
                }

                properties[param.Name] = new
                {
                    type = GetJsonSchemaType(param.ParameterType),
                    description = paramDescription
                };
            }

            return new
            {
                name = toolName,
                description = description,
                inputSchema = new
                {
                    type = "object",
                    properties = properties,
                    required = required.ToArray()
                }
            };
        }).ToList();

        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            result = new
            {
                tools = tools
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await writer.WriteLineAsync(json);
        _logger.LogDebug("📤 Sent tools list with {ToolCount} tools", tools.Count);
    }

    private async Task HandleToolsCall(StreamWriter writer, int? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var toolName = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"]?.AsObject();

        if (string.IsNullOrEmpty(toolName))
        {
            await SendErrorResponse(writer, id, -32602, "Invalid params", "Missing tool name");
            return;
        }

        _logger.LogDebug("🔧 Calling tool: {ToolName} with args: {Args}", toolName, 
            arguments?.ToJsonString() ?? "{}");

        if (!_tools.TryGetValue(toolName, out var toolInfo))
        {
            await SendErrorResponse(writer, id, -32602, "Invalid params", $"Unknown tool: {toolName}");
            return;
        }

        // Wait for initialization to complete before executing tools
        _logger.LogDebug("⏳ Waiting for data initialization to complete...");
        await _initializationSemaphore.WaitAsync(cancellationToken);
        _initializationSemaphore.Release(); // Release immediately so other calls can proceed
        _logger.LogDebug("✅ Data initialization complete, executing tool");

        try
        {
            var (method, declaringType, parameters_) = toolInfo;

            // Prepare method arguments
            var args = new List<object?>();
            
            foreach (var param in parameters_)
            {
                if (param.Name == "serverData")
                {
                    args.Add(_serverData);
                }
                else if (param.Name == "conflictDetector")
                {
                    args.Add(_conflictDetector);
                }
                else if (arguments?.ContainsKey(param.Name) == true)
                {
                    var value = ConvertArgument(arguments[param.Name], param.ParameterType);
                    args.Add(value);
                }
                else if (param.HasDefaultValue)
                {
                    args.Add(param.DefaultValue);
                }
                else
                {
                    // Required parameter is missing
                    await SendErrorResponse(writer, id, -32602, "Invalid params", 
                        $"Missing required parameter: {param.Name}");
                    return;
                }
            }

            // Invoke the tool method
            var result = method.Invoke(null, args.ToArray());
            
            string jsonResult;
            if (result is string stringResult)
            {
                jsonResult = stringResult;
            }
            else
            {
                jsonResult = JsonSerializer.Serialize(result);
            }

            var response = new
            {
                jsonrpc = "2.0",
                id = id,
                result = new
                {
                    content = new[]
                    {
                        new
                        {
                            type = "text",
                            text = jsonResult
                        }
                    }
                }
            };

            var responseJson = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            await writer.WriteLineAsync(responseJson);
            
            _logger.LogDebug("✅ Tool {ToolName} executed successfully", toolName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Tool {ToolName} execution failed", toolName);
            await SendErrorResponse(writer, id, -32603, "Internal error", $"Tool execution failed: {ex.Message}");
        }
    }

    private async Task SendErrorResponse(StreamWriter writer, int? id, int code, string message, string? data = null)
    {
        var response = new
        {
            jsonrpc = "2.0",
            id = id,
            error = new
            {
                code = code,
                message = message,
                data = data
            }
        };

        var json = JsonSerializer.Serialize(response, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await writer.WriteLineAsync(json);
        _logger.LogDebug("❌ Sent error response: {Code} - {Message}", code, message);
    }

    private static Dictionary<string, (MethodInfo method, Type declaringType, ParameterInfo[] parameters)> DiscoverTools()
    {
        var tools = new Dictionary<string, (MethodInfo, Type, ParameterInfo[])>();

        // Get all types that contain MCP tools
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
            typeof(ModWorkflowTools)
        };

        foreach (var type in toolTypes)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() != null);

            foreach (var method in methods)
            {
                var toolName = ConvertMethodNameToToolName(method.Name);
                var parameters = method.GetParameters();
                
                tools[toolName] = (method, type, parameters);
            }
        }

        return tools;
    }

    private static string ConvertMethodNameToToolName(string methodName)
    {
        // Convert PascalCase to snake_case
        return string.Concat(methodName.Select((c, i) => 
            i > 0 && char.IsUpper(c) ? "_" + char.ToLower(c).ToString() : char.ToLower(c).ToString()));
    }

    private static string GetJsonSchemaType(Type type)
    {
        var underlyingType = Nullable.GetUnderlyingType(type) ?? type;

        if (underlyingType == typeof(string))
            return "string";
        if (underlyingType == typeof(int) || underlyingType == typeof(long) || underlyingType == typeof(short))
            return "integer";
        if (underlyingType == typeof(double) || underlyingType == typeof(float) || underlyingType == typeof(decimal))
            return "number";
        if (underlyingType == typeof(bool))
            return "boolean";
        if (underlyingType.IsArray || (underlyingType.IsGenericType && underlyingType.GetGenericTypeDefinition() == typeof(List<>)))
            return "array";

        return "string"; // Default fallback
    }

    private static object? ConvertArgument(JsonNode? jsonValue, Type targetType)
    {
        if (jsonValue == null)
            return null;

        var underlyingType = Nullable.GetUnderlyingType(targetType) ?? targetType;

        try
        {
            if (underlyingType == typeof(string))
                return jsonValue.GetValue<string>();
            if (underlyingType == typeof(int))
                return jsonValue.GetValue<int>();
            if (underlyingType == typeof(long))
                return jsonValue.GetValue<long>();
            if (underlyingType == typeof(double))
                return jsonValue.GetValue<double>();
            if (underlyingType == typeof(float))
                return jsonValue.GetValue<float>();
            if (underlyingType == typeof(bool))
                return jsonValue.GetValue<bool>();
            if (underlyingType == typeof(decimal))
                return jsonValue.GetValue<decimal>();

            // For complex types, try to deserialize from JSON
            return JsonSerializer.Deserialize(jsonValue.ToJsonString(), targetType);
        }
        catch (Exception)
        {
            // If conversion fails, return the string representation
            return jsonValue.ToString();
        }
    }

    private static string GetServerVersion()
    {
        var informationalVersion = typeof(McpServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informationalVersion?.Split('+')[0] ?? "0.0.0-local";
    }
}
