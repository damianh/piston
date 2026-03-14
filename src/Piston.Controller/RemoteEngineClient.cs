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
/// </summary>
internal sealed class RemoteEngineClient : IEngineClient
{
    private readonly NamedPipeClientTransport _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonRpcResponse>> _pending = new();
    private int _requestCounter;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveLoop;

    private StateSnapshotNotification? _currentSnapshot;
    private readonly Lock _snapshotLock = new();

    public event Action<StateSnapshotNotification>? StateChanged;
    public event Action<PhaseChangedNotification>? PhaseChanged;
    public event Action<TestProgressNotification>? TestProgressChanged;
    public event Action<BuildErrorNotification>? BuildError;

    public StateSnapshotNotification? CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
                return _currentSnapshot;
        }
    }

    public RemoteEngineClient(string pipeName)
    {
        _transport = new NamedPipeClientTransport(pipeName);
    }

    /// <summary>Connects to the headless controller and starts the background receive loop.</summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        await _transport.ConnectAsync(ct).ConfigureAwait(false);
        // Use a standalone CTS — not linked to the connect-time token — so that the
        // receive loop lifetime is independent of the token used during connection setup.
        _receiveCts  = new CancellationTokenSource();
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
    }

    // ── IEngineClient commands ────────────────────────────────────────────────

    public Task StartAsync(string solutionPath) =>
        SendCommandAsync(ProtocolMethods.EngineStart,
            JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                new { solutionPath }, JsonRpcSerializer.Options)));

    public Task ForceRunAsync() =>
        SendCommandAsync(ProtocolMethods.EngineForceRun, null);

    public void Stop() =>
        _ = SendCommandAsync(ProtocolMethods.EngineStop, null);

    public Task SetFilterAsync(string? filter) =>
        SendCommandAsync(ProtocolMethods.EngineSetFilter,
            JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
                new { filter }, JsonRpcSerializer.Options)));

    public Task ClearResultsAsync() =>
        SendCommandAsync(ProtocolMethods.EngineClearResults, null);

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
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

        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task SendCommandAsync(string method, JsonNode? @params)
    {
        var id      = Interlocked.Increment(ref _requestCounter).ToString();
        var request = new JsonRpcRequest(id, method, @params);
        var bytes   = JsonRpcSerializer.Serialize(request);
        var tcs     = new TaskCompletionSource<JsonRpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        _pending[id] = tcs;

        try
        {
            await _transport.SendAsync(bytes, CancellationToken.None).ConfigureAwait(false);

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
                    raw = await _transport.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
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
