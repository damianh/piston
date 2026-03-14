using System.Text.Json.Nodes;

namespace Piston.Protocol.JsonRpc;

/// <summary>JSON-RPC 2.0 request message (client → server, expects response).</summary>
public sealed record JsonRpcRequest(
    string Id,
    string Method,
    JsonNode? Params = null
)
{
    public string JsonRpc { get; } = "2.0";
}

/// <summary>JSON-RPC 2.0 response message (server → client, in reply to a request).</summary>
public sealed record JsonRpcResponse(
    string Id,
    JsonNode? Result = null,
    JsonRpcError? Error = null
)
{
    public string JsonRpc { get; } = "2.0";
}

/// <summary>JSON-RPC 2.0 notification message (server → client, no response expected).</summary>
public sealed record JsonRpcNotification(
    string Method,
    JsonNode? Params = null
)
{
    public string JsonRpc { get; } = "2.0";
}
