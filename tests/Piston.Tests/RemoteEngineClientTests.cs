// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Hosting;
using Piston.Hosting.Protocol;
using Piston.Protocol;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Tests;

public sealed class RemoteEngineClientTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static string UniquePipeName() =>
        $"piston-remote-{Guid.NewGuid():N}";

    private static (NamedPipeListener listener, ProtocolRouter router, StubEngine engine)
        CreateServer(string pipeName)
    {
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        return (listener, router, engine);
    }

    // ── Connect → CurrentSnapshot populated ──────────────────────────────────

    [Fact]
    public async Task Connect_CurrentSnapshotPopulated()
    {
        var pipeName = UniquePipeName();
        var (listener, router, _) = CreateServer(pipeName);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        // Wait for snapshot
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        Assert.NotNull(client.CurrentSnapshot);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── ForceRunAsync → request sent, response received ──────────────────────

    [Fact]
    public async Task ForceRunAsync_SendsRequest_EngineForceRunCalled()
    {
        var pipeName = UniquePipeName();
        var (listener, router, engine) = CreateServer(pipeName);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        // Wait for initial snapshot
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        await client.ForceRunAsync();

        Assert.True(engine.ForceRunCalled);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── daemon/info and daemon/shutdown ──────────────────────────────────────

    [Fact]
    public async Task DaemonControl_InfoAndShutdownReachTheHost()
    {
        var pipeName = UniquePipeName();
        var (_, router, _) = CreateServer(pipeName);
        var info = new DaemonInfoResponse(1234, "/w/App.slnx", pipeName, DateTimeOffset.UnixEpoch, "1.2.3",
            "http://localhost:5199", "http://localhost:5200");
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.SetDaemonControl(new DaemonControl(() => info, () => shutdown.TrySetResult()));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        Assert.Equal(info, await client.GetDaemonInfoAsync());
        await client.ShutdownDaemonAsync();
        await shutdown.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    [Fact]
    public async Task DaemonControl_UnavailableWithoutHost()
    {
        var pipeName = UniquePipeName();
        var (_, router, _) = CreateServer(pipeName);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        _ = await Assert.ThrowsAsync<InvalidOperationException>(client.GetDaemonInfoAsync);
        _ = await Assert.ThrowsAsync<InvalidOperationException>(client.ShutdownDaemonAsync);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── StateChanged event fires on notification ──────────────────────────────

    [Fact]
    public async Task ServerStateChange_StateChangedEventFires()
    {
        var pipeName = UniquePipeName();
        var (listener, router, engine) = CreateServer(pipeName);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new RemoteEngineClient(pipeName);
        var stateChangeFired = 0;
        client.StateChanged += _ => Interlocked.Increment(ref stateChangeFired);

        await client.ConnectAsync(cts.Token);

        // Wait for initial snapshot
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        var countBefore = stateChangeFired;

        // Trigger state change on server
        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () => Volatile.Read(ref stateChangeFired) > countBefore,
            TimeSpan.FromSeconds(5));

        Assert.True(stateChangeFired > countBefore);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── DisposeAsync disconnects cleanly ──────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DisconnectsCleanly_NoException()
    {
        var pipeName = UniquePipeName();
        var (listener, router, _) = CreateServer(pipeName);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        // Wait for snapshot
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        // Should not throw
        await client.DisposeAsync();

        await cts.CancelAsync();
        await router.DisposeAsync();
    }

    // ── Server shuts down → client handles gracefully ────────────────────────

    [Fact]
    public async Task ServerShutdown_ClientHandlesGracefully()
    {
        var pipeName = UniquePipeName();
        var (listener, router, _) = CreateServer(pipeName);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var routerCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = Task.Run(() => router.RunAsync(routerCts.Token), routerCts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        // Wait for snapshot
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        // Shut down the server
        await routerCts.CancelAsync();
        await router.DisposeAsync();

        // Client should handle this gracefully (not crash)
        await Task.Delay(500, _ct); // allow time for pipe closure to propagate

        // Dispose client — should not throw
        await client.DisposeAsync();
        await cts.CancelAsync();
    }

    // ── Server restart → client reconnects ───────────────────────────────────

    [Fact]
    public async Task ServerRestart_ClientReconnects_SnapshotRestored()
    {
        var pipeName = UniquePipeName();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Start first server
        var (_, router1, _) = CreateServer(pipeName);
        var router1Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(() => router1.RunAsync(router1Cts.Token), router1Cts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        // Wait for initial snapshot
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectionState.Connected, client.ConnectionState);

        // Shut down first server
        await router1Cts.CancelAsync();
        await router1.DisposeAsync();

        // Wait for client to detect disconnection
        await WaitForConditionAsync(
            () => client.ConnectionState != ConnectionState.Connected,
            TimeSpan.FromSeconds(5));

        // Start second server on the same pipe name
        var (_, router2, _) = CreateServer(pipeName);
        var router2Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(() => router2.RunAsync(router2Cts.Token), router2Cts.Token);

        // Wait for client to reconnect and receive new snapshot
        await WaitForConditionAsync(
            () => client.ConnectionState == ConnectionState.Connected,
            TimeSpan.FromSeconds(15));

        Assert.Equal(ConnectionState.Connected, client.ConnectionState);
        Assert.NotNull(client.CurrentSnapshot);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router2Cts.CancelAsync();
        await router2.DisposeAsync();
    }

    // ── ConnectionState fires Disconnected → Reconnecting → Connected ─────────

    [Fact]
    public async Task ConnectionStateChanged_FiresOnDisconnectAndReconnect()
    {
        var pipeName = UniquePipeName();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (_, router1, _) = CreateServer(pipeName);
        var router1Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(() => router1.RunAsync(router1Cts.Token), router1Cts.Token);

        var client = new RemoteEngineClient(pipeName);

        var stateSequence = new System.Collections.Concurrent.ConcurrentQueue<ConnectionState>();
        client.ConnectionStateChanged += s => stateSequence.Enqueue(s);

        await client.ConnectAsync(cts.Token);
        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        // Shut down first server — should trigger Disconnected then Reconnecting
        await router1Cts.CancelAsync();
        await router1.DisposeAsync();

        await WaitForConditionAsync(
            () => stateSequence.Contains(ConnectionState.Disconnected),
            TimeSpan.FromSeconds(5));

        await WaitForConditionAsync(
            () => stateSequence.Contains(ConnectionState.Reconnecting),
            TimeSpan.FromSeconds(5));

        // Start second server — should trigger Connected
        var (_, router2, _) = CreateServer(pipeName);
        var router2Cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(() => router2.RunAsync(router2Cts.Token), router2Cts.Token);

        await WaitForConditionAsync(
            () => stateSequence.Contains(ConnectionState.Connected),
            TimeSpan.FromSeconds(15));

        Assert.Contains(ConnectionState.Disconnected, stateSequence);
        Assert.Contains(ConnectionState.Reconnecting, stateSequence);
        Assert.Contains(ConnectionState.Connected, stateSequence);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router2Cts.CancelAsync();
        await router2.DisposeAsync();
    }

    // ── SendCommand while disconnected throws InvalidOperationException ────────

    [Fact]
    public async Task SendCommand_WhileDisconnected_ThrowsInvalidOperation()
    {
        var pipeName = UniquePipeName();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var (_, router, _) = CreateServer(pipeName);
        var routerCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = Task.Run(() => router.RunAsync(routerCts.Token), routerCts.Token);

        var client = new RemoteEngineClient(pipeName);
        await client.ConnectAsync(cts.Token);

        await WaitForConditionAsync(() => client.CurrentSnapshot is not null, TimeSpan.FromSeconds(5));

        // Shut down server to force disconnect
        await routerCts.CancelAsync();
        await router.DisposeAsync();

        await WaitForConditionAsync(
            () => client.ConnectionState != ConnectionState.Connected,
            TimeSpan.FromSeconds(5));

        // Attempting to send a command while not connected should throw
        _ = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ForceRunAsync());

        await cts.CancelAsync();
        await client.DisposeAsync();
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(50);
        }
        Assert.Fail("Condition was not met within timeout.");
    }

    // ── Stub engine ───────────────────────────────────────────────────────────

    private sealed class StubEngine : IEngine
    {
        public bool ForceRunCalled { get; private set; }
        public PistonState State { get; } = new();

        public Task StartAsync(string solutionPath) => Task.CompletedTask;

        public Task ForceRunAsync()
        {
            ForceRunCalled = true;
            return Task.CompletedTask;
        }

        public void Stop() { }

        public void SetFilter(string? filter) { }

        public void ClearResults() { }

        public void Dispose() { }

        public void TriggerStateChange() => State.NotifyChanged();
    }
}
