using System.Text.Json.Nodes;

namespace Piston.Protocol.JsonRpc;

/// <summary>Standard JSON-RPC 2.0 error codes.</summary>
public static class JsonRpcErrorCodes
{
    public const int ParseError     = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams  = -32602;
    public const int InternalError  = -32603;
}

/// <summary>JSON-RPC 2.0 error object.</summary>
public sealed record JsonRpcError(int Code, string Message, JsonNode? Data = null);
