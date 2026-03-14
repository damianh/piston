using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Piston.Controller.Mapping;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

namespace Piston.Controller.Protocol;

/// <summary>
/// Accepts named pipe client connections, manages <see cref="ClientSession"/> instances,
/// and broadcasts engine state notifications to all connected clients.
/// </summary>
internal sealed class ProtocolRouter : IAsyncDisposable
{
    private readonly IEngine _engine;
    private readonly NamedPipeListener _listener;
    private readonly ConcurrentDictionary<string, ClientSession> _sessions = new();
    private int _sessionCounter;

    public int ClientCount => _sessions.Count;

    public ProtocolRouter(IEngine engine, NamedPipeListener listener)
    {
        _engine   = engine;
        _listener = listener;
    }

    /// <summary>
    /// Starts the accept loop and subscribes to engine state changes.
    /// Blocks until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _engine.State.StateChanged += OnEngineStateChanged;
        try
        {
            await foreach (var stream in _listener.AcceptClientsAsync(ct).ConfigureAwait(false))
            {
                var sessionId  = $"session-{Interlocked.Increment(ref _sessionCounter)}";
                var dispatcher = new EngineCommandDispatcher(_engine);
                var session    = new ClientSession(stream, sessionId, dispatcher);

                _sessions[sessionId] = session;

                // Send initial snapshot before starting the read loop
                try
                {
                    var snapshotNotification = BuildStateSnapshot();
                    await session.SendNotificationAsync(snapshotNotification, ct).ConfigureAwait(false);
                }
                catch
                {
                    _sessions.TryRemove(sessionId, out _);
                    continue;
                }

                // Run session in the background
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await session.RunAsync(ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        _sessions.TryRemove(sessionId, out _);
                    }
                }, ct);
            }
        }
        finally
        {
            _engine.State.StateChanged -= OnEngineStateChanged;
        }
    }

    private void OnEngineStateChanged()
    {
        var stateSnapshot = _engine.State.ToSnapshot();

        BroadcastNotification(ToNotification(ProtocolMethods.EngineStateSnapshot, stateSnapshot));

        BroadcastNotification(ToNotification(
            ProtocolMethods.EnginePhaseChanged,
            new PhaseChangedNotification(stateSnapshot.Phase, null)));

        if (_engine.State.Phase == PistonPhase.Testing)
        {
            BroadcastNotification(ToNotification(
                ProtocolMethods.TestsProgress,
                new TestProgressNotification(
                    stateSnapshot.InProgressSuites,
                    stateSnapshot.CompletedTests,
                    stateSnapshot.TotalExpectedTests)));
        }

        if (_engine.State.Phase == PistonPhase.Error && stateSnapshot.LastBuild is not null)
        {
            BroadcastNotification(ToNotification(
                ProtocolMethods.BuildError,
                new BuildErrorNotification(stateSnapshot.LastBuild)));
        }
    }

    private void BroadcastNotification(JsonRpcNotification notification)
    {
        foreach (var (id, session) in _sessions)
        {
            _ = session.SendNotificationAsync(notification, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _sessions.TryRemove(id, out _);
                }, TaskScheduler.Default);
        }
    }

    private JsonRpcNotification BuildStateSnapshot()
    {
        var snapshot = _engine.State.ToSnapshot();
        return ToNotification(ProtocolMethods.EngineStateSnapshot, snapshot);
    }

    private static JsonRpcNotification ToNotification<T>(string method, T payload)
    {
        var paramsNode = JsonNode.Parse(
            System.Text.Json.JsonSerializer.Serialize(payload, JsonRpcSerializer.Options));
        return new JsonRpcNotification(method, paramsNode);
    }

    public ValueTask DisposeAsync()
    {
        _engine.State.StateChanged -= OnEngineStateChanged;
        return _listener.DisposeAsync();
    }
}
