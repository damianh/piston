using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Piston.Hosting.Services;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting.Mapping;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

namespace Piston.Hosting.Protocol;

/// <summary>
/// Accepts named pipe and WebSocket client connections, manages session instances,
/// and broadcasts engine state notifications to all connected clients.
/// Implements <see cref="IActivityEventSink"/> to fan in activity events from all subsystems.
/// </summary>
public sealed class ProtocolRouter(IEngine engine, NamedPipeListener listener)
    : IActivityEventSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClientSession> _pipeSessions = new();
    private readonly ConcurrentDictionary<string, WebSocketClientSession> _wsSessions = new();
    private int _sessionCounter;
    private DiagnosticWatcherService? _diagnosticWatcher;
    private McpCallTracker? _mcpCallTracker;

    public int ClientCount => _pipeSessions.Count + _wsSessions.Count;

    public void SetDiagnosticWatcher(DiagnosticWatcherService watcher) =>
        _diagnosticWatcher = watcher;

    public void SetMcpCallTracker(McpCallTracker tracker) =>
        _mcpCallTracker = tracker;

    private EngineCommandDispatcher CreateDispatcher()
    {
        var dispatcher = new EngineCommandDispatcher(engine);
        if (_diagnosticWatcher is not null)
            dispatcher.SetDiagnosticWatcher(_diagnosticWatcher);
        if (_mcpCallTracker is not null)
            dispatcher.SetMcpCallTracker(_mcpCallTracker);
        return dispatcher;
    }

    /// <summary>
    /// Starts the named pipe accept loop and subscribes to engine state changes.
    /// Blocks until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        engine.State.StateChanged += OnEngineStateChanged;
        try
        {
            await foreach (var stream in listener.AcceptClientsAsync(ct).ConfigureAwait(false))
            {
                var sessionId  = $"pipe-{Interlocked.Increment(ref _sessionCounter)}";
                var dispatcher = CreateDispatcher();
                var session    = new ClientSession(stream, sessionId, dispatcher);

                _pipeSessions[sessionId] = session;

                // Send initial snapshot before starting the read loop
                try
                {
                    var snapshotNotification = BuildStateSnapshot();
                    await session.SendNotificationAsync(snapshotNotification, ct).ConfigureAwait(false);
                }
                catch
                {
                    _pipeSessions.TryRemove(sessionId, out _);
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
                        _pipeSessions.TryRemove(sessionId, out _);
                    }
                }, ct);
            }
        }
        finally
        {
            engine.State.StateChanged -= OnEngineStateChanged;
        }
    }

    /// <summary>
    /// Accepts a WebSocket connection, sends the initial state snapshot, and runs the session.
    /// Returns when the WebSocket closes or <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task AddWebSocketSessionAsync(WebSocket webSocket, CancellationToken ct)
    {
        var sessionId  = $"ws-{Interlocked.Increment(ref _sessionCounter)}";
        var dispatcher = CreateDispatcher();
        var session    = new WebSocketClientSession(webSocket, sessionId, dispatcher);

        _wsSessions[sessionId] = session;

        try
        {
            var snapshotNotification = BuildStateSnapshot();
            await session.SendNotificationAsync(snapshotNotification, ct).ConfigureAwait(false);
        }
        catch
        {
            _wsSessions.TryRemove(sessionId, out _);
            return;
        }

        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _wsSessions.TryRemove(sessionId, out _);
        }
    }

    private void OnEngineStateChanged()
    {
        var stateSnapshot = engine.State.ToSnapshot();

        BroadcastNotification(ToNotification(ProtocolMethods.EngineStateSnapshot, stateSnapshot));

        BroadcastNotification(ToNotification(
            ProtocolMethods.EnginePhaseChanged,
            new PhaseChangedNotification(stateSnapshot.Phase, null)));

        if (engine.State.Phase == PistonPhase.Testing)
        {
            BroadcastNotification(ToNotification(
                ProtocolMethods.TestsProgress,
                new TestProgressNotification(
                    stateSnapshot.InProgressSuites,
                    stateSnapshot.CompletedTests,
                    stateSnapshot.TotalExpectedTests)));
        }

        if (engine.State.Phase == PistonPhase.Error && stateSnapshot.LastBuild is not null)
        {
            BroadcastNotification(ToNotification(
                ProtocolMethods.BuildError,
                new BuildErrorNotification(stateSnapshot.LastBuild)));
        }
    }

    private void BroadcastNotification(JsonRpcNotification notification)
    {
        foreach (var (id, session) in _pipeSessions)
        {
            _ = session.SendNotificationAsync(notification, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _pipeSessions.TryRemove(id, out _);
                }, TaskScheduler.Default);
        }

        foreach (var (id, session) in _wsSessions)
        {
            _ = session.SendNotificationAsync(notification, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        _wsSessions.TryRemove(id, out _);
                }, TaskScheduler.Default);
        }
    }

    /// <inheritdoc />
    public void Emit(ActivityEvent activity)
    {
        BroadcastNotification(ToNotification(ProtocolMethods.ActivityEvent, activity));
    }

    private JsonRpcNotification BuildStateSnapshot()
    {
        var snapshot = engine.State.ToSnapshot();
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
        engine.State.StateChanged -= OnEngineStateChanged;
        return listener.DisposeAsync();
    }
}
