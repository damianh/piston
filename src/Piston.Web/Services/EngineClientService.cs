using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Web.Services;

/// <summary>
/// Connects to the Piston daemon via WebSocket JSON-RPC and dispatches
/// engine state notifications to subscribers.
/// </summary>
public sealed class EngineClientService : IAsyncDisposable
{
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;

    public event Action<StateSnapshotNotification>? StateChanged;
    public event Action? ConnectionStateChanged;

    public StateSnapshotNotification? CurrentSnapshot { get; private set; }
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public bool IsConnecting { get; private set; }

    private int _requestCounter;

    public async Task StartAsync(string wsUrl)
    {
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectLoopAsync(wsUrl, _cts.Token));
        await Task.CompletedTask;
    }

    private async Task ConnectLoopAsync(string wsUrl, CancellationToken ct)
    {
        var backoffMs = 500;
        const int MaxBackoffMs = 10_000;

        while (!ct.IsCancellationRequested)
        {
            IsConnecting = true;
            ConnectionStateChanged?.Invoke();

            _webSocket?.Dispose();
            _webSocket = new ClientWebSocket();

            try
            {
                await _webSocket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);
                IsConnecting = false;
                backoffMs = 500;
                ConnectionStateChanged?.Invoke();

                await ReceiveLoopAsync(_webSocket, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Connection failed — retry with backoff
            }

            IsConnecting = false;
            ConnectionStateChanged?.Invoke();

            try
            {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[4 * 1024 * 1024];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            int totalBytes = 0;
            WebSocketReceiveResult result;

            do
            {
                var segment = new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes);
                result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);
                totalBytes += result.Count;
            }
            while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            if (result.MessageType != WebSocketMessageType.Text)
                continue;

            var raw = new ReadOnlyMemory<byte>(buffer, 0, totalBytes);
            HandleMessage(raw);
        }
    }

    private void HandleMessage(ReadOnlyMemory<byte> raw)
    {
        try
        {
            var msg = JsonRpcSerializer.DeserializeMessage(raw);
            if (msg is not JsonRpcNotification notification)
                return;

            if (notification.Method == ProtocolMethods.EngineStateSnapshot)
            {
                var snapshot = notification.Params?.Deserialize<StateSnapshotNotification>(JsonRpcSerializer.Options);
                if (snapshot is not null)
                {
                    CurrentSnapshot = snapshot;
                    StateChanged?.Invoke(snapshot);
                }
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    public Task SendCommandAsync(string method) => SendCommandAsync(method, (object?)null);

    public async Task SendCommandAsync(string method, object? @params)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return;

        var id = Interlocked.Increment(ref _requestCounter).ToString();
        var paramsNode = @params is null
            ? null
            : System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(@params, JsonRpcSerializer.Options));

        var request = new JsonRpcRequest(id, method, paramsNode);
        var bytes = JsonRpcSerializer.Serialize(request);

        try
        {
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // Connection lost — reconnect loop will handle it
        }
    }

    public Task ForceRunAsync() => SendCommandAsync(ProtocolMethods.EngineForceRun);
    public Task ClearResultsAsync() => SendCommandAsync(ProtocolMethods.EngineClearResults);
    public Task SetFilterAsync(string? filter) => SendCommandAsync(ProtocolMethods.EngineSetFilter, new SetFilterCommand(filter));

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }

        _webSocket?.Dispose();
    }
}
