using System.Text.Json.Nodes;
using Piston.Protocol.JsonRpc;

namespace Piston.Controller.Protocol;

/// <summary>
/// Represents a single connected client on the server side.
/// Runs a read loop that dispatches incoming JSON-RPC requests and writes responses back.
/// </summary>
internal sealed class ClientSession
{
    private readonly Stream _stream;
    private readonly ICommandDispatcher _dispatcher;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public string SessionId { get; }

    public ClientSession(Stream stream, string sessionId, ICommandDispatcher dispatcher)
    {
        _stream     = stream;
        SessionId   = sessionId;
        _dispatcher = dispatcher;
    }

    /// <summary>
    /// Runs the receive/dispatch loop. Returns when the stream closes or an error occurs.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadOnlyMemory<byte>? raw;
                try
                {
                    raw = await MessageFramer.ReadMessageAsync(_stream, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    // Stream error — session ends
                    break;
                }

                if (raw is null)
                    break; // EOF — client disconnected

                await HandleMessageAsync(raw.Value, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _stream.Dispose();
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
            await MessageFramer.WriteMessageAsync(_stream, bytes, ct).ConfigureAwait(false);
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
            {
                // Unexpected message type from client — ignore
                return;
            }
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
            result = await _dispatcher.HandleCommandAsync(request.Method, request.Params, ct)
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
            await MessageFramer.WriteMessageAsync(_stream, responseBytes, ct).ConfigureAwait(false);
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
            await MessageFramer.WriteMessageAsync(_stream, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
