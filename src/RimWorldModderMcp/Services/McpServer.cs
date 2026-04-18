using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RimWorldModderMcp.Models;

namespace RimWorldModderMcp.Services;

public class McpServer : BackgroundService
{
    private readonly ILogger<McpServer> _logger;
    private readonly ServerData _serverData;
    private readonly ToolExecutor _toolExecutor;
    private readonly ProjectContext _projectContext;
    private readonly SemaphoreSlim _initializationSemaphore = new(0, 1);

    public McpServer(
        ILogger<McpServer> logger,
        ServerData serverData,
        ToolExecutor toolExecutor,
        ProjectContext projectContext)
    {
        _logger = logger;
        _serverData = serverData;
        _toolExecutor = toolExecutor;
        _projectContext = projectContext;
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
        _logger.LogDebug("📋 Listing {ToolCount} available tools", _toolExecutor.Tools.Count);

        var tools = _toolExecutor.Tools.Select(kvp =>
        {
            var toolName = kvp.Key;
            var (method, parameters) = kvp.Value;
            
            // Get description from attribute
            var description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? 
                             $"Execute {toolName} tool";

            // Build input schema from method parameters
            var properties = new Dictionary<string, object>();
            var required = new List<string>();

            foreach (var param in parameters)
            {
                if (IsInjectedParameter(param.ParameterType)) continue;
                if (string.IsNullOrWhiteSpace(param.Name)) continue;

                var paramDescription = param.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? 
                                     $"{param.Name} parameter";

                var isRequired = !param.HasDefaultValue && Nullable.GetUnderlyingType(param.ParameterType) == null;
                if (isRequired)
                {
                    required.Add(param.Name);
                }

                properties[param.Name] = new
                {
                    type = ToolExecutor.GetJsonSchemaType(param.ParameterType),
                    description = paramDescription
                };
            }

            AddStandardToolProperties(properties);

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

        if (!_toolExecutor.Tools.ContainsKey(toolName))
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
            var execution = _toolExecutor.Execute(
                toolName,
                ToArgumentDictionary(arguments),
                new ToolExecutionOptions
                {
                    OutputMode = ToolOutputFormatter.ParseMode(arguments?["outputMode"]?.GetValue<string>() ?? _projectContext.OutputMode),
                    PageSize = arguments?["pageSize"]?.GetValue<int>() ?? _projectContext.PageSize,
                    PageOffset = arguments?["pageOffset"]?.GetValue<int>() ?? _projectContext.PageOffset,
                    HandleResults = arguments?["handleResults"]?.GetValue<bool>() ?? _projectContext.HandleResults
                });

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
                            text = execution.Text
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

    private static bool IsInjectedParameter(Type type) =>
        type == typeof(ServerData) ||
        type == typeof(ConflictDetector) ||
        type == typeof(ProjectContext) ||
        type == typeof(ResultHandleStore);

    private static void AddStandardToolProperties(Dictionary<string, object> properties)
    {
        if (!properties.ContainsKey("outputMode"))
        {
            properties["outputMode"] = new
            {
                type = "string",
                description = "Output mode: compact, normal, or detailed"
            };
        }

        if (!properties.ContainsKey("pageSize"))
        {
            properties["pageSize"] = new
            {
                type = "integer",
                description = "Maximum items returned per array-valued section"
            };
        }

        if (!properties.ContainsKey("pageOffset"))
        {
            properties["pageOffset"] = new
            {
                type = "integer",
                description = "Offset to apply before paging array-valued sections"
            };
        }

        if (!properties.ContainsKey("handleResults"))
        {
            properties["handleResults"] = new
            {
                type = "boolean",
                description = "Store a retrievable result handle for this response"
            };
        }
    }

    private static Dictionary<string, object?> ToArgumentDictionary(JsonObject? arguments)
    {
        if (arguments == null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        }

        var dictionary = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in arguments)
        {
            dictionary[property.Key] = property.Value;
        }

        return dictionary;
    }

    private static string GetServerVersion()
    {
        var informationalVersion = typeof(McpServer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        return informationalVersion?.Split('+')[0] ?? "0.0.0-local";
    }
}
