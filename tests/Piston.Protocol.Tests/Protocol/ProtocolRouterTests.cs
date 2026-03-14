using System.Text.Json.Nodes;
using Piston.Controller.Protocol;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Protocol.Tests.Protocol;

public sealed class ProtocolRouterTests
{
    private static string UniquePipeName() =>
        $"piston-router-{Guid.NewGuid():N}";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<(NamedPipeClientTransport client, Task readerTask, List<object> received)>
        ConnectClientAsync(string pipeName, CancellationToken ct)
    {
        var client   = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(ct);

        var received = new List<object>();
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var bytes = await client.ReceiveAsync(ct);
                    var msg   = JsonRpcSerializer.DeserializeMessage(bytes);
                    lock (received) received.Add(msg);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }, ct);

        return (client, readerTask, received);
    }

    // ── Client connects → receives state snapshot ────────────────────────────

    [Fact]
    public async Task ClientConnects_ReceivesStateSnapshot()
    {
        var pipeName = UniquePipeName();
        var engine   = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router   = new Piston.Controller.Protocol.ProtocolRouter(engine, listener);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var routerTask = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client, readerTask, received) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for snapshot
        await WaitForConditionAsync(() => { lock (received) return received.Count >= 1; }, TimeSpan.FromSeconds(5));

        lock (received)
        {
            var first = received[0];
            var notification = Assert.IsType<JsonRpcNotification>(first);
            Assert.Equal(ProtocolMethods.EngineStateSnapshot, notification.Method);
        }

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── Client sends engine/forceRun → receives success response ────────────

    [Fact]
    public async Task ClientSendsForceRun_EngineForceRunCalled_ReceivesResponse()
    {
        var pipeName = UniquePipeName();
        var engine   = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router   = new Piston.Controller.Protocol.ProtocolRouter(engine, listener);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);

        // Drain the initial snapshot
        await client.ReceiveAsync(cts.Token);

        // Send forceRun request
        var request = new JsonRpcRequest("req-1", ProtocolMethods.EngineForceRun);
        await client.SendAsync(JsonRpcSerializer.Serialize(request), cts.Token);

        // Read response
        var responseBytes = await client.ReceiveAsync(cts.Token);
        var response      = JsonRpcSerializer.DeserializeResponse(responseBytes);

        Assert.Equal("req-1", response.Id);
        Assert.Null(response.Error);
        Assert.True(engine.ForceRunCalled);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── Unknown method → -32601 error response ────────────────────────────────

    [Fact]
    public async Task ClientSendsUnknownMethod_ReceivesMethodNotFoundError()
    {
        var pipeName = UniquePipeName();
        var engine   = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router   = new Piston.Controller.Protocol.ProtocolRouter(engine, listener);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);

        // Drain snapshot
        await client.ReceiveAsync(cts.Token);

        var request = new JsonRpcRequest("req-x", "unknown/method");
        await client.SendAsync(JsonRpcSerializer.Serialize(request), cts.Token);

        var responseBytes = await client.ReceiveAsync(cts.Token);
        var response      = JsonRpcSerializer.DeserializeResponse(responseBytes);

        Assert.NotNull(response.Error);
        Assert.Equal(JsonRpcErrorCodes.MethodNotFound, response.Error!.Code);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── Engine state change → client receives notification ────────────────────

    [Fact]
    public async Task EngineStateChange_ClientReceivesNotification()
    {
        var pipeName = UniquePipeName();
        var engine   = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router   = new Piston.Controller.Protocol.ProtocolRouter(engine, listener);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client, _, received) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for initial snapshot
        await WaitForConditionAsync(() => { lock (received) return received.Count >= 1; }, TimeSpan.FromSeconds(5));

        var countBefore = received.Count;

        // Trigger state change
        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () => { lock (received) return received.Count > countBefore; },
            TimeSpan.FromSeconds(5));

        lock (received)
        {
            // After state change, router broadcasts: stateSnapshot + phaseChanged
            // We just verify that at least one stateSnapshot notification was received
            var snapshots = received.Skip(countBefore)
                .OfType<JsonRpcNotification>()
                .Where(n => n.Method == ProtocolMethods.EngineStateSnapshot)
                .ToList();
            Assert.NotEmpty(snapshots);
        }

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── Two clients → both receive notifications ──────────────────────────────

    [Fact]
    public async Task TwoClients_BothReceiveNotifications()
    {
        var pipeName = UniquePipeName();
        var engine   = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router   = new Piston.Controller.Protocol.ProtocolRouter(engine, listener);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client1, _, received1) = await ConnectClientAsync(pipeName, cts.Token);
        var (client2, _, received2) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for both initial snapshots
        await WaitForConditionAsync(
            () => { lock (received1) return received1.Count >= 1; },
            TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(
            () => { lock (received2) return received2.Count >= 1; },
            TimeSpan.FromSeconds(5));

        var count1 = received1.Count;
        var count2 = received2.Count;

        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () =>
            {
                lock (received1) lock (received2)
                    return received1.Count > count1 && received2.Count > count2;
            },
            TimeSpan.FromSeconds(5));

        Assert.True(received1.Count > count1);
        Assert.True(received2.Count > count2);

        await cts.CancelAsync();
        await client1.DisposeAsync();
        await client2.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── One client disconnects, remaining client unaffected ───────────────────

    [Fact]
    public async Task OneClientDisconnects_RemainingClientUnaffected()
    {
        var pipeName = UniquePipeName();
        var engine   = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router   = new Piston.Controller.Protocol.ProtocolRouter(engine, listener);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client1, _, received1) = await ConnectClientAsync(pipeName, cts.Token);
        var (client2, _, received2) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for initial snapshots
        await WaitForConditionAsync(
            () => { lock (received1) return received1.Count >= 1; }, TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(
            () => { lock (received2) return received2.Count >= 1; }, TimeSpan.FromSeconds(5));

        // Disconnect client1
        await client1.DisposeAsync();

        // Small delay for cleanup
        await Task.Delay(200);

        var count2 = received2.Count;

        // Trigger state change — client2 should still receive it
        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () => { lock (received2) return received2.Count > count2; },
            TimeSpan.FromSeconds(5));

        Assert.True(received2.Count > count2);

        await cts.CancelAsync();
        await client2.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
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
