using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Piston.Protocol.JsonRpc;

/// <summary>
/// Serializes and deserializes JSON-RPC 2.0 messages to/from <see cref="ReadOnlyMemory{T}"/>.
/// All methods are thread-safe.
/// </summary>
public static class JsonRpcSerializer
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling              = JsonNumberHandling.AllowReadingFromString,
        Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        WriteIndented               = false,
    };

    // ── Serialization ─────────────────────────────────────────────────────────

    public static ReadOnlyMemory<byte> Serialize(JsonRpcRequest request)
    {
        var envelope = new RequestEnvelope
        {
            JsonRpc = request.JsonRpc,
            Id      = request.Id,
            Method  = request.Method,
            Params  = request.Params,
        };
        return Encode(envelope);
    }

    public static ReadOnlyMemory<byte> Serialize(JsonRpcResponse response)
    {
        var envelope = new ResponseEnvelope
        {
            JsonRpc = response.JsonRpc,
            Id      = response.Id,
            Result  = response.Result,
            Error   = response.Error is null ? null : new ErrorEnvelope
            {
                Code    = response.Error.Code,
                Message = response.Error.Message,
                Data    = response.Error.Data,
            },
        };
        return Encode(envelope);
    }

    public static ReadOnlyMemory<byte> Serialize(JsonRpcNotification notification)
    {
        var envelope = new NotificationEnvelope
        {
            JsonRpc = notification.JsonRpc,
            Method  = notification.Method,
            Params  = notification.Params,
        };
        return Encode(envelope);
    }

    // ── Deserialization ───────────────────────────────────────────────────────

    /// <summary>
    /// Determines whether raw bytes represent a request, response, or notification,
    /// then returns the corresponding typed object.
    /// </summary>
    public static object DeserializeMessage(ReadOnlyMemory<byte> data)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        var hasId     = root.TryGetProperty("id", out _);
        var hasMethod = root.TryGetProperty("method", out _);
        var hasResult = root.TryGetProperty("result", out _);
        var hasError  = root.TryGetProperty("error", out _);

        if (hasMethod && hasId)
            return DeserializeRequest(data);

        if (hasResult || hasError)
            return DeserializeResponse(data);

        // A response that has id but neither method, result, nor error (e.g. null result, no error)
        if (hasId && !hasMethod)
            return DeserializeResponse(data);

        if (hasMethod && !hasId)
            return DeserializeNotification(data);

        throw new JsonException("Cannot determine JSON-RPC message type: missing 'method' or 'result'/'error' fields.");
    }

    public static JsonRpcRequest DeserializeRequest(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<RequestEnvelope>(data.Span, Options)
            ?? throw new JsonException("Failed to deserialize JSON-RPC request.");
        return new JsonRpcRequest(envelope.Id!, envelope.Method!, envelope.Params);
    }

    public static JsonRpcResponse DeserializeResponse(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<ResponseEnvelope>(data.Span, Options)
            ?? throw new JsonException("Failed to deserialize JSON-RPC response.");

        JsonRpcError? error = null;
        if (envelope.Error is not null)
            error = new JsonRpcError(envelope.Error.Code, envelope.Error.Message ?? string.Empty, envelope.Error.Data);

        return new JsonRpcResponse(envelope.Id!, envelope.Result, error);
    }

    public static JsonRpcNotification DeserializeNotification(ReadOnlyMemory<byte> data)
    {
        var envelope = JsonSerializer.Deserialize<NotificationEnvelope>(data.Span, Options)
            ?? throw new JsonException("Failed to deserialize JSON-RPC notification.");
        return new JsonRpcNotification(envelope.Method!, envelope.Params);
    }

    /// <summary>
    /// Deserializes typed params from a <see cref="JsonNode"/>. Returns the default for <typeparamref name="T"/> if <paramref name="paramsNode"/> is null.
    /// </summary>
    public static T? DeserializeParams<T>(JsonNode? paramsNode)
    {
        if (paramsNode is null)
            return default;

        return paramsNode.Deserialize<T>(Options);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static ReadOnlyMemory<byte> Encode<T>(T value)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        return new ReadOnlyMemory<byte>(json);
    }

    // Private envelope types for serialization (avoid polluting public API with nullable warnings)

    private sealed class RequestEnvelope
    {
        public string? JsonRpc { get; set; }
        public string? Id     { get; set; }
        public string? Method { get; set; }
        public JsonNode? Params { get; set; }
    }

    private sealed class ResponseEnvelope
    {
        public string? JsonRpc { get; set; }
        public string? Id     { get; set; }
        public JsonNode? Result { get; set; }
        public ErrorEnvelope? Error { get; set; }
    }

    private sealed class NotificationEnvelope
    {
        public string? JsonRpc { get; set; }
        public string? Method { get; set; }
        public JsonNode? Params { get; set; }
    }

    private sealed class ErrorEnvelope
    {
        public int Code { get; set; }
        public string? Message { get; set; }
        public JsonNode? Data { get; set; }
    }
}
