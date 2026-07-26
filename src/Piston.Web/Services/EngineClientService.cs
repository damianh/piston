using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Web.Services;

/// <summary>
/// Connects to the Piston daemon via WebSocket JSON-RPC and dispatches
/// engine state notifications and activity events to subscribers.
/// </summary>
public sealed class EngineClientService : IAsyncDisposable
{
    private const int ActivityLogCapacity = 500;

    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;

    private readonly List<ActivityEvent> _activityLog = [];
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pendingRequests = new();

    public event Action<StateSnapshotNotification>? StateChanged;
    public event Action? ConnectionStateChanged;
    public event Action<ActivityEvent>? ActivityEventReceived;

    public StateSnapshotNotification? CurrentSnapshot { get; private set; }
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;
    public bool IsConnecting { get; private set; }

    public IReadOnlyList<ActivityEvent> ActivityLog
    {
        get { lock (_activityLog) return _activityLog.ToList(); }
    }

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
        const int InitialBufferSize = 64 * 1024;
        const int MaxMessageSize = 16 * 1024 * 1024;
        var buffer = new byte[InitialBufferSize];

        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            int totalBytes = 0;
            WebSocketReceiveResult? result = null;

            do
            {
                if (totalBytes >= buffer.Length)
                {
                    if (buffer.Length >= MaxMessageSize)
                        break; // discard oversized message
                    var newSize = Math.Min(buffer.Length * 2, MaxMessageSize);
                    Array.Resize(ref buffer, newSize);
                }

                var segment = new ArraySegment<byte>(buffer, totalBytes, buffer.Length - totalBytes);
                result = await ws.ReceiveAsync(segment, ct).ConfigureAwait(false);
                totalBytes += result.Count;
            }
            while (!result.EndOfMessage);

            if (result is null || totalBytes >= MaxMessageSize)
                continue; // skip oversized or empty messages

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

            if (msg is JsonRpcResponse response)
            {
                HandleResponse(response);
                return;
            }

            if (msg is not JsonRpcNotification notification)
                return;

            HandleNotification(notification);
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private void HandleResponse(JsonRpcResponse response)
    {
        if (_pendingRequests.TryRemove(response.Id, out var tcs))
            tcs.TrySetResult(response.Result);
    }

    private void HandleNotification(JsonRpcNotification notification)
    {
        if (notification.Method == ProtocolMethods.EngineStateSnapshot)
        {
            var snapshot = notification.Params?.Deserialize<StateSnapshotNotification>(JsonRpcSerializer.Options);
            if (snapshot is not null)
            {
                CurrentSnapshot = snapshot;
                StateChanged?.Invoke(snapshot);
            }
            return;
        }

        if (notification.Method == ProtocolMethods.ActivityEvent)
        {
            var evt = notification.Params?.Deserialize<ActivityEvent>(JsonRpcSerializer.Options);
            if (evt is not null)
            {
                AppendActivityEvent(evt);
                ActivityEventReceived?.Invoke(evt);
            }
        }
    }

    private void AppendActivityEvent(ActivityEvent evt)
    {
        lock (_activityLog)
        {
            if (_activityLog.Count >= ActivityLogCapacity)
                _activityLog.RemoveAt(0);
            _activityLog.Add(evt);
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
            : JsonNode.Parse(JsonSerializer.Serialize(@params, JsonRpcSerializer.Options));

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

    public Task<JsonNode?> SendRequestAsync(string method) => SendRequestAsync(method, null);

    public async Task<JsonNode?> SendRequestAsync(string method, object? @params)
    {
        if (_webSocket?.State != WebSocketState.Open)
            return null;

        var id = Interlocked.Increment(ref _requestCounter).ToString();
        var paramsNode = @params is null
            ? null
            : JsonNode.Parse(JsonSerializer.Serialize(@params, JsonRpcSerializer.Options));

        var request = new JsonRpcRequest(id, method, paramsNode);
        var bytes = JsonRpcSerializer.Serialize(request);

        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[id] = tcs;

        try
        {
            await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            _pendingRequests.TryRemove(id, out _);
            return null;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        timeoutCts.Token.Register(() => tcs.TrySetResult(null));
        return await tcs.Task.ConfigureAwait(false);
    }

    public async Task<DiagnosticsResponse?> GetDiagnosticsAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.DiagnosticsGetAll).ConfigureAwait(false);
        if (result is null)
            return null;
        return result.Deserialize<DiagnosticsResponse>(JsonRpcSerializer.Options);
    }

    public async Task<McpCallLogResponse?> GetMcpCallLogAsync()
    {
        var result = await SendRequestAsync(ProtocolMethods.McpGetCallLog).ConfigureAwait(false);
        if (result is null)
            return null;
        return result.Deserialize<McpCallLogResponse>(JsonRpcSerializer.Options);
    }    public Task ForceRunAsync() => SendCommandAsync(ProtocolMethods.EngineForceRun);
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
