// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json.Nodes;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting.Configuration;
using Piston.Hosting.Mapping;
using Piston.Hosting.Services;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

namespace Piston.Hosting.Protocol;

/// <summary>
/// Accepts named pipe and WebSocket client connections, manages session instances,
/// and broadcasts engine state notifications to all connected clients.
/// Implements <see cref="IActivityEventSink"/> to map engine activity to wire notifications.
/// </summary>
public sealed class ProtocolRouter(IEngine engine, NamedPipeListener listener)
    : IActivityEventSink, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ClientSession> _pipeSessions = new();
    private readonly ConcurrentDictionary<string, WebSocketClientSession> _wsSessions = new();
    private int _sessionCounter;
    private McpCallTracker? _mcpCallTracker;
    private DaemonControl? _daemonControl;
    private UserSettingsStore? _userSettings;
    private readonly ActivityHistory _activityHistory = new();

    public int ClientCount => _pipeSessions.Count + _wsSessions.Count;

    public void SetMcpCallTracker(McpCallTracker tracker) =>
        _mcpCallTracker = tracker;

    public void SetDaemonControl(DaemonControl control) =>
        _daemonControl = control;

    public void SetUserSettingsStore(UserSettingsStore store) =>
        _userSettings = store;

    private EngineCommandDispatcher CreateDispatcher(bool allowDaemonControl)
    {
        var dispatcher = new EngineCommandDispatcher(engine);
        dispatcher.SetActivityHistory(_activityHistory);
        if (_mcpCallTracker is not null)
        {
            dispatcher.SetMcpCallTracker(_mcpCallTracker);
        }

        // Settings are typed and validated, so browser sessions may read and change them.
        if (_userSettings is not null)
        {
            dispatcher.SetUserSettingsStore(_userSettings);
        }

        // Process control is limited to local pipe clients (the CLI); browser-reachable
        // WebSocket sessions cannot shut down the daemon.
        if (allowDaemonControl && _daemonControl is not null)
        {
            dispatcher.SetDaemonControl(_daemonControl);
        }

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
                var sessionId = $"pipe-{Interlocked.Increment(ref _sessionCounter)}";
                var dispatcher = CreateDispatcher(allowDaemonControl: true);
                var session = new ClientSession(stream, sessionId, dispatcher);

                _pipeSessions[sessionId] = session;

                // Send initial snapshot before starting the read loop
                try
                {
                    var snapshotNotification = BuildStateSnapshot();
                    await session.SendNotificationAsync(snapshotNotification, ct).ConfigureAwait(false);
                }
                catch
                {
                    _ = _pipeSessions.TryRemove(sessionId, out _);
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
                        _ = _pipeSessions.TryRemove(sessionId, out _);
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
        var sessionId = $"ws-{Interlocked.Increment(ref _sessionCounter)}";
        var dispatcher = CreateDispatcher(allowDaemonControl: false);
        var session = new WebSocketClientSession(webSocket, sessionId, dispatcher);

        _wsSessions[sessionId] = session;

        try
        {
            var snapshotNotification = BuildStateSnapshot();
            await session.SendNotificationAsync(snapshotNotification, ct).ConfigureAwait(false);
        }
        catch
        {
            _ = _wsSessions.TryRemove(sessionId, out _);
            return;
        }

        try
        {
            await session.RunAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _ = _wsSessions.TryRemove(sessionId, out _);
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
                    {
                        _ = _pipeSessions.TryRemove(id, out _);
                    }
                }, TaskScheduler.Default);
        }

        foreach (var (id, session) in _wsSessions)
        {
            _ = session.SendNotificationAsync(notification, CancellationToken.None)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _ = _wsSessions.TryRemove(id, out _);
                    }
                }, TaskScheduler.Default);
        }
    }

    /// <inheritdoc />
    public void Emit(EngineActivityEvent activity) => Emit(activity.ToDto());

    public void Emit(ActivityEvent activity)
    {
        activity = activity.Id is null ? activity with { Id = Guid.NewGuid().ToString("N") } : activity;
        _activityHistory.Add(activity);
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
