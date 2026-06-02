using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Piston.Protocol.JsonRpc;

namespace Piston.Cli.Protocol;

/// <summary>
/// Represents a single WebSocket client connection on the server side.
/// Each WebSocket message is treated as one complete JSON-RPC message (no NDJSON framing).
/// </summary>
internal sealed class WebSocketClientSession(WebSocket webSocket, string sessionId, ICommandDispatcher dispatcher)
{
    private const int MaxMessageSize = 4 * 1024 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string SessionId { get; } = sessionId;

    /// <summary>
    /// Runs the receive/dispatch loop. Returns when the WebSocket closes or an error occurs.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[MaxMessageSize];

        try
        {
            while (!ct.IsCancellationRequested && webSocket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result;
                int totalBytes = 0;

                try
                {
                    do
                    {
                        var segment = new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes);
                        result = await webSocket.ReceiveAsync(segment, ct).ConfigureAwait(false);
                        totalBytes += result.Count;

                        if (totalBytes >= MaxMessageSize && !result.EndOfMessage)
                        {
                            await webSocket.CloseAsync(
                                WebSocketCloseStatus.MessageTooBig,
                                "Message too large",
                                CancellationToken.None).ConfigureAwait(false);
                            return;
                        }
                    }
                    while (!result.EndOfMessage);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                var raw = new ReadOnlyMemory<byte>(buffer, 0, totalBytes);
                await HandleMessageAsync(raw, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (webSocket.State == WebSocketState.Open || webSocket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Session ended",
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch { /* best-effort close */ }
            }

            webSocket.Dispose();
        }
    }

    /// <summary>
    /// Sends a notification to the client. Thread-safe.
    /// </summary>
    public async Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken ct)
    {
        var bytes = JsonRpcSerializer.Serialize(notification);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task HandleMessageAsync(ReadOnlyMemory<byte> raw, CancellationToken ct)
    {
        JsonRpcRequest request;
        try
        {
            var msg = JsonRpcSerializer.DeserializeMessage(raw);
            if (msg is not JsonRpcRequest req)
                return; // Unexpected message type — ignore

            request = req;
        }
        catch (Exception ex)
        {
            await SendErrorResponseAsync(
                id: null,
                code: JsonRpcErrorCodes.ParseError,
                message: $"Parse error: {ex.Message}",
                ct).ConfigureAwait(false);
            return;
        }

        JsonNode? result;
        try
        {
            result = await dispatcher.HandleCommandAsync(request.Method, request.Params, ct)
                .ConfigureAwait(false);
        }
        catch (JsonRpcException rpcEx)
        {
            await SendErrorResponseAsync(request.Id, rpcEx.Code, rpcEx.Message, ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            await SendErrorResponseAsync(
                request.Id,
                JsonRpcErrorCodes.InternalError,
                $"Internal error: {ex.Message}",
                ct).ConfigureAwait(false);
            return;
        }

        var response = new JsonRpcResponse(request.Id, result);
        var responseBytes = JsonRpcSerializer.Serialize(response);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await webSocket.SendAsync(responseBytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task SendErrorResponseAsync(string? id, int code, string message, CancellationToken ct)
    {
        var response = new JsonRpcResponse(
            id ?? string.Empty,
            Error: new JsonRpcError(code, message));
        var bytes = JsonRpcSerializer.Serialize(response);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
