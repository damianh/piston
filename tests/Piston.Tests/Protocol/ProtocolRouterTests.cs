// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Models;
using Piston.Hosting.Protocol;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Tests.Protocol;

public sealed class ProtocolRouterTests
{
    private static string UniquePipeName() =>
        $"piston-router-{Guid.NewGuid():N}";

    // ── Helpers ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task EngineActivity_IsMappedAndBroadcastToClient()
    {
        var pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var router = new ProtocolRouter(new StubEngine(), new NamedPipeListener(pipeName));
        var routerTask = router.RunAsync(cts.Token);
        await using var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);
        _ = await client.ReceiveAsync(cts.Token);
        var activity = new BuildCompletedActivityEvent("/repo/Test.slnx", false, 12.5, ["CS0001"])
        {
            Timestamp = DateTimeOffset.UnixEpoch,
        };
        var proxy = new ActivityEventSinkProxy();
        proxy.SetSink(router);

        proxy.Emit(activity);

        var notification = JsonRpcSerializer.DeserializeNotification(await client.ReceiveAsync(cts.Token));
        Assert.Equal(ProtocolMethods.ActivityEvent, notification.Method);
        Assert.Equal(ActivityEventTypes.BuildCompleted, notification.Params!["type"]!.GetValue<string>());
        Assert.Equal(DateTimeOffset.UnixEpoch, notification.Params["timestamp"]!.GetValue<DateTimeOffset>());
        Assert.Equal("/repo/Test.slnx", notification.Params["solutionPath"]!.GetValue<string>());
        Assert.False(notification.Params["data"]!["succeeded"]!.GetValue<bool>());
        Assert.Equal("CS0001", notification.Params["data"]!["errors"]![0]!.GetValue<string>());

        await cts.CancelAsync();
        await routerTask;
    }

    [Fact]
    public async Task ActivityGetLog_BackfillsEventsEmittedBeforeClientConnected()
    {
        var pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var router = new ProtocolRouter(new StubEngine(), new NamedPipeListener(pipeName));
        var routerTask = router.RunAsync(cts.Token);
        router.Emit(ActivityEventFactory.McpToolCall("/repo/Test.slnx", "get_test_results", null, "ok", 1.5, true));
        router.Emit(ActivityEventFactory.BuildCompleted("/repo/Test.slnx", true, 900, []));

        await using var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);
        _ = await client.ReceiveAsync(cts.Token);
        await client.SendAsync(JsonRpcSerializer.Serialize(new JsonRpcRequest("log-1", ProtocolMethods.ActivityGetLog)), cts.Token);

        var response = JsonRpcSerializer.DeserializeResponse(await client.ReceiveAsync(cts.Token));

        Assert.Null(response.Error);
        var log = JsonRpcSerializer.DeserializeParams<ActivityLogResponse>(response.Result)!;
        Assert.Equal(
            [ActivityEventTypes.McpToolCall, ActivityEventTypes.BuildCompleted],
            log.Events.Select(e => e.Type));
        Assert.Equal("get_test_results", log.Events[0].Data!["toolName"]!.GetValue<string>());

        await cts.CancelAsync();
        await routerTask;
    }

    [Fact]
    public async Task Emit_AssignsDistinctIds_AndHistoryReplaysSameIdsAsLiveEvents()
    {
        var pipeName = UniquePipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var router = new ProtocolRouter(new StubEngine(), new NamedPipeListener(pipeName));
        var routerTask = router.RunAsync(cts.Token);
        await using var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);
        _ = await client.ReceiveAsync(cts.Token);
        var first = ActivityEventFactory.McpToolCall("/repo/Test.slnx", "get_test_results", null, "ok", 1.5, true);
        var second = ActivityEventFactory.McpToolCall("/repo/Test.slnx", "run_tests", null, "ok", 2.5, true);
        first = first with { Timestamp = DateTimeOffset.UnixEpoch };
        second = second with { Timestamp = DateTimeOffset.UnixEpoch };

        router.Emit(first);
        router.Emit(second);

        var liveIds = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var notification = JsonRpcSerializer.DeserializeNotification(await client.ReceiveAsync(cts.Token));
            liveIds.Add(notification.Params!["id"]!.GetValue<string>());
        }

        await client.SendAsync(JsonRpcSerializer.Serialize(new JsonRpcRequest("log-1", ProtocolMethods.ActivityGetLog)), cts.Token);
        var response = JsonRpcSerializer.DeserializeResponse(await client.ReceiveAsync(cts.Token));
        var log = JsonRpcSerializer.DeserializeParams<ActivityLogResponse>(response.Result)!;

        Assert.NotEqual(liveIds[0], liveIds[1]);
        Assert.Equal(liveIds, log.Events.Select(e => e.Id));

        await cts.CancelAsync();
        await routerTask;
    }

    private static async Task<(NamedPipeClientTransport client, Task readerTask, List<object> received)>
        ConnectClientAsync(string pipeName, CancellationToken ct)
    {
        var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(ct);

        var received = new List<object>();
        var readerTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var bytes = await client.ReceiveAsync(ct);
                    var msg = JsonRpcSerializer.DeserializeMessage(bytes);
                    lock (received)
                    {
                        received.Add(msg);
                    }
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
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var routerTask = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client, readerTask, received) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for snapshot
        await WaitForConditionAsync(() => { lock (received) { return received.Count >= 1; } }, TimeSpan.FromSeconds(5));

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

    // ── Client sends engine/forceRun or engine/reset → receives success response ──

    [Theory]
    [InlineData(ProtocolMethods.EngineForceRun)]
    [InlineData(ProtocolMethods.EngineReset)]
    public async Task ClientSendsEngineCommand_EngineMethodCalled_ReceivesResponse(string method)
    {
        var pipeName = UniquePipeName();
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);

        // Drain the initial snapshot
        _ = await client.ReceiveAsync(cts.Token);

        var request = new JsonRpcRequest("req-1", method);
        await client.SendAsync(JsonRpcSerializer.Serialize(request), cts.Token);

        // Read response
        var responseBytes = await client.ReceiveAsync(cts.Token);
        var response = JsonRpcSerializer.DeserializeResponse(responseBytes);

        Assert.Equal("req-1", response.Id);
        Assert.Null(response.Error);
        Assert.Equal(method == ProtocolMethods.EngineForceRun, engine.ForceRunCalled);
        Assert.Equal(method == ProtocolMethods.EngineReset, engine.ResetCalled);

        await cts.CancelAsync();
        await client.DisposeAsync();
        await router.DisposeAsync();
    }

    // ── Unknown method → -32601 error response ────────────────────────────────

    [Theory]
    [InlineData("unknown/method")]
    [InlineData("diagnostics/getAll")]
    public async Task ClientSendsUnknownMethod_ReceivesMethodNotFoundError(string method)
    {
        var pipeName = UniquePipeName();
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var client = new NamedPipeClientTransport(pipeName);
        await client.ConnectAsync(cts.Token);

        // Drain snapshot
        _ = await client.ReceiveAsync(cts.Token);

        var request = new JsonRpcRequest("req-x", method);
        await client.SendAsync(JsonRpcSerializer.Serialize(request), cts.Token);

        var responseBytes = await client.ReceiveAsync(cts.Token);
        var response = JsonRpcSerializer.DeserializeResponse(responseBytes);

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
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client, _, received) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for initial snapshot
        await WaitForConditionAsync(() => { lock (received) { return received.Count >= 1; } }, TimeSpan.FromSeconds(5));

        var countBefore = received.Count;

        // Trigger state change
        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () => { lock (received) { return received.Count > countBefore; } },
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
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client1, _, received1) = await ConnectClientAsync(pipeName, cts.Token);
        var (client2, _, received2) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for both initial snapshots
        await WaitForConditionAsync(
            () => { lock (received1) { return received1.Count >= 1; } },
            TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(
            () => { lock (received2) { return received2.Count >= 1; } },
            TimeSpan.FromSeconds(5));

        var count1 = received1.Count;
        var count2 = received2.Count;

        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () =>
            {
                lock (received1) lock (received2)
                {
                    return received1.Count > count1 && received2.Count > count2;
                }
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
        var engine = new StubEngine();
        var listener = new NamedPipeListener(pipeName);
        var router = new ProtocolRouter(engine, listener);
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        _ = Task.Run(() => router.RunAsync(cts.Token), cts.Token);

        var (client1, _, received1) = await ConnectClientAsync(pipeName, cts.Token);
        var (client2, _, received2) = await ConnectClientAsync(pipeName, cts.Token);

        // Wait for initial snapshots
        await WaitForConditionAsync(
            () => { lock (received1) { return received1.Count >= 1; } }, TimeSpan.FromSeconds(5));
        await WaitForConditionAsync(
            () => { lock (received2) { return received2.Count >= 1; } }, TimeSpan.FromSeconds(5));

        // Disconnect client1
        await client1.DisposeAsync();

        // Small delay for cleanup
        await Task.Delay(200);

        var count2 = received2.Count;

        // Trigger state change — client2 should still receive it
        engine.TriggerStateChange();

        await WaitForConditionAsync(
            () => { lock (received2) { return received2.Count > count2; } },
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

        public bool ResetCalled { get; private set; }

        public Task ResetAsync()
        {
            ResetCalled = true;
            return Task.CompletedTask;
        }

        public void Stop() { }

        public void SetFilter(string? filter) { }

        public void ClearResults() { }

        public void Dispose() { }

        public void TriggerStateChange() => State.NotifyChanged();
    }
}
