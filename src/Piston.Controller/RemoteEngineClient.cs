using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Piston.Protocol;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

namespace Piston.Controller;

/// <summary>
/// <see cref="IEngineClient"/> implementation that communicates with a headless controller
/// over a named pipe via JSON-RPC 2.0.
/// Automatically reconnects with exponential backoff when the connection is lost.
/// </summary>
internal sealed class RemoteEngineClient : IEngineClient
{
    private readonly string _pipeName;
    private NamedPipeClientTransport? _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private int _requestCounter;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;
    private bool _disposed;

    private ConnectionState _connectionState = ConnectionState.Disconnected;
    private readonly Lock _stateLock = new();

    private StateSnapshotNotification? _currentSnapshot;
    private readonly Lock _snapshotLock = new();

    public event Action<StateSnapshotNotification>? StateChanged;
    public event Action<PhaseChangedNotification>? PhaseChanged;
    public event Action<TestProgressNotification>? TestProgressChanged;
    public event Action<BuildErrorNotification>? BuildError;
    public event Action<ConnectionState>? ConnectionStateChanged;

    public StateSnapshotNotification? CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
                return _currentSnapshot;
        }
    }

    public ConnectionState ConnectionState
    {
        get
        {
            lock (_stateLock)
                return _connectionState;
        }
    }

    public RemoteEngineClient(string pipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>Connects to the headless controller and starts the background receive loop.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        _transport   = new NamedPipeClientTransport(_pipeName);
        await _transport.ConnectAsync(ct).ConfigureAwait(false);

        SetConnectionState(ConnectionState.Connected);

        // Use a standalone CTS — not linked to the connect-time token — so that the
        // receive loop lifetime is independent of the token used during connection setup.
        _receiveCts  = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    // ── IEngineClient commands ────────────────────────────────────────────────

    public Task StartAsync(string solutionPath) =>
        SendCommandAsync(ProtocolMethods.EngineStart,
            JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                new StartCommandParams(solutionPath), JsonRpcSerializer.Options)));

    public Task ForceRunAsync() =>
        SendCommandAsync(ProtocolMethods.EngineForceRun, null);

    public void Stop() =>
        _ = SendCommandAsync(ProtocolMethods.EngineStop, null);

    public Task SetFilterAsync(string? filter) =>
        SendCommandAsync(ProtocolMethods.EngineSetFilter,
            JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                new SetFilterCommandParams(filter), JsonRpcSerializer.Options)));

    public Task ClearResultsAsync() =>
        SendCommandAsync(ProtocolMethods.EngineClearResults, null);

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }

        if (_receiveCts is not null)
        {
            await _receiveCts.CancelAsync().ConfigureAwait(false);
            try
            {
                if (_receiveLoop is not null)
                    await _receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }

            _receiveCts.Dispose();
        }

        if (_transport is not null)
            await _transport.DisposeAsync().ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task SendCommandAsync(string method, JsonNode? @params)
    {
        if (ConnectionState != ConnectionState.Connected)
            throw new InvalidOperationException("Not connected to the headless controller.");

        var id      = Interlocked.Increment(ref _requestCounter).ToString();
        var request = new JsonRpcRequest(id, method, @params);
        var bytes   = JsonRpcSerializer.Serialize(request);
        var tcs     = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = tcs;

        try
        {
            await _transport!.SendAsync(bytes, CancellationToken.None).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            cts.Token.Register(() => tcs.TrySetCanceled());

            var response = await tcs.Task.ConfigureAwait(false);

            if (response.Error is not null)
                throw new InvalidOperationException($"JSON-RPC error {response.Error.Code}: {response.Error.Message}");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReadOnlyMemory<byte> raw;
                try
                {
                    raw = await _transport!.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    // Pipe closed — start reconnection
                    break;
                }

                DispatchIncoming(raw);
            }
        }
        finally
        {
            // Cancel all pending requests
            foreach (var tcs in _pending.Values)
                tcs.TrySetException(new IOException("Connection to headless controller was lost."));
            _pending.Clear();
        }

        // Start reconnection if not disposed
        bool isDisposed;
        lock (_stateLock) isDisposed = _disposed;

        if (!isDisposed && !ct.IsCancellationRequested)
            await ReconnectLoopAsync(ct).ConfigureAwait(false);
    }

    private async Task ReconnectLoopAsync(CancellationToken ct)
    {
        SetConnectionState(ConnectionState.Disconnected);

        // Dispose old transport
        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
            _transport = null;
        }

        SetConnectionState(ConnectionState.Reconnecting);

        // Exponential backoff: 500ms, 1s, 2s, 4s, 8s, capped at 10s
        var delay = TimeSpan.FromMilliseconds(500);
        var maxDelay = TimeSpan.FromSeconds(10);

        while (!ct.IsCancellationRequested)
        {
            bool isDisposed;
            lock (_stateLock) isDisposed = _disposed;
            if (isDisposed) return;

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = delay.TotalSeconds >= maxDelay.TotalSeconds
                ? maxDelay
                : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(TimeSpan.FromSeconds(5));

                _transport = new NamedPipeClientTransport(_pipeName);
                await _transport.ConnectAsync(connectCts.Token).ConfigureAwait(false);

                SetConnectionState(ConnectionState.Connected);

                // Restart receive loop on the existing _receiveCts token
                _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts!.Token));
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Connection attempt failed; loop and retry
                if (_transport is not null)
                {
                    await _transport.DisposeAsync().ConfigureAwait(false);
                    _transport = null;
                }
            }
        }
    }

    private void SetConnectionState(ConnectionState state)
    {
        lock (_stateLock)
            _connectionState = state;

        ConnectionStateChanged?.Invoke(state);
    }

    private void DispatchIncoming(ReadOnlyMemory<byte> raw)
    {
        try
        {
            var msg = JsonRpcSerializer.DeserializeMessage(raw);

            if (msg is JsonRpcNotification notification)
            {
                HandleNotification(notification);
                return;
            }

            if (msg is JsonRpcResponse response)
            {
                if (_pending.TryGetValue(response.Id, out var tcs))
                    tcs.TrySetResult(response);
                return;
            }
        }
        catch
        {
            // Ignore malformed messages
        }
    }

    private void HandleNotification(JsonRpcNotification notification)
    {
        switch (notification.Method)
        {
            case ProtocolMethods.EngineStateSnapshot:
            {
                var snapshot = JsonRpcSerializer.DeserializeParams<StateSnapshotNotification>(notification.Params);
                if (snapshot is null) return;
                lock (_snapshotLock)
                    _currentSnapshot = snapshot;
                StateChanged?.Invoke(snapshot);
                break;
            }
            case ProtocolMethods.EnginePhaseChanged:
            {
                var n = JsonRpcSerializer.DeserializeParams<PhaseChangedNotification>(notification.Params);
                if (n is not null) PhaseChanged?.Invoke(n);
                break;
            }
            case ProtocolMethods.TestsProgress:
            {
                var n = JsonRpcSerializer.DeserializeParams<TestProgressNotification>(notification.Params);
                if (n is not null) TestProgressChanged?.Invoke(n);
                break;
            }
            case ProtocolMethods.BuildError:
            {
                var n = JsonRpcSerializer.DeserializeParams<BuildErrorNotification>(notification.Params);
                if (n is not null) BuildError?.Invoke(n);
                break;
            }
        }
    }
}
