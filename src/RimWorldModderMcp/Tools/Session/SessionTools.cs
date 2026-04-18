using System.ComponentModel;
using System.Text.Json;
using RimWorldModderMcp.Attributes;
using RimWorldModderMcp.Services;

namespace RimWorldModderMcp.Tools.Session;

public static class SessionTools
{
    [McpServerTool, Description("Use when a prior tool returned a result handle and you want the full stored payload back.")]
    public static string GetResultByHandle(
        ResultHandleStore resultHandleStore,
        [Description("Result handle returned in a prior tool response")] string handle)
    {
        var payload = resultHandleStore.GetPayload(handle);
        if (payload == null)
        {
            return JsonSerializer.Serialize(new { error = $"Result handle '{handle}' was not found" });
        }

        var metadata = resultHandleStore.GetMetadata(handle);
        return JsonSerializer.Serialize(new
        {
            handle,
            metadata,
            result = payload
        });
    }
}
