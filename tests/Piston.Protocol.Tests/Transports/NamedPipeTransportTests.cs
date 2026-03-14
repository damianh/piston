using System.IO.Pipes;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Protocol.Tests.Transports;

public sealed class NamedPipeTransportTests
{
    private static string UniquePipeName() =>
        $"piston-test-{Guid.NewGuid():N}";

    // ── Connect ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClientConnects_ServerAccepts_Successfully()
    {
        var pipeName  = UniquePipeName();
        var server    = new NamedPipeServerTransport(pipeName);
        var client    = new NamedPipeClientTransport(pipeName);
        var cts       = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptTask   = server.AcceptClientAsync(cts.Token);
        await client.ConnectAsync(cts.Token);
        var serverStream = await acceptTask;

        Assert.NotNull(serverStream);

        serverStream.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // ── Client sends, server receives ────────────────────────────────────────

    [Fact]
    public async Task ClientSends_ServerReceives()
    {
        var pipeName = UniquePipeName();
        var server   = new NamedPipeServerTransport(pipeName);
        var client   = new NamedPipeClientTransport(pipeName);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptTask   = server.AcceptClientAsync(cts.Token);
        await client.ConnectAsync(cts.Token);
        var serverStream = await acceptTask;

        var message = System.Text.Encoding.UTF8.GetBytes("{\"test\":1}");

        // Named pipes on Windows require concurrent reader and writer — run both in parallel.
        var sendTask    = client.SendAsync(message, cts.Token);
        var receiveTask = MessageFramer.ReadMessageAsync(serverStream, cts.Token);
        await Task.WhenAll(sendTask, receiveTask);

        var received = await receiveTask;

        Assert.NotNull(received);
        Assert.Equal(message, received!.Value.ToArray());

        serverStream.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // ── Server sends, client receives ────────────────────────────────────────

    [Fact]
    public async Task ServerSends_ClientReceives()
    {
        var pipeName = UniquePipeName();
        var server   = new NamedPipeServerTransport(pipeName);
        var client   = new NamedPipeClientTransport(pipeName);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptTask   = server.AcceptClientAsync(cts.Token);
        await client.ConnectAsync(cts.Token);
        var serverStream = await acceptTask;

        var message = System.Text.Encoding.UTF8.GetBytes("{\"notification\":\"hello\"}");

        // Named pipes on Windows require concurrent reader and writer — run both in parallel.
        var writeTask   = MessageFramer.WriteMessageAsync(serverStream, message, cts.Token);
        var receiveTask = client.ReceiveAsync(cts.Token);
        await Task.WhenAll(writeTask, receiveTask);

        var received = await receiveTask;
        Assert.Equal(message, received.ToArray());

        serverStream.Dispose();
        await client.DisposeAsync();
        await server.DisposeAsync();
    }

    // ── Client disconnects — server read returns null ─────────────────────────

    [Fact]
    public async Task ClientDisconnects_ServerReadReturnsNull()
    {
        var pipeName = UniquePipeName();
        var server   = new NamedPipeServerTransport(pipeName);
        var client   = new NamedPipeClientTransport(pipeName);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var acceptTask   = server.AcceptClientAsync(cts.Token);
        await client.ConnectAsync(cts.Token);
        var serverStream = await acceptTask;

        // Dispose client — closes the pipe
        await client.DisposeAsync();

        var result = await MessageFramer.ReadMessageAsync(serverStream, cts.Token);
        Assert.Null(result);

        serverStream.Dispose();
        await server.DisposeAsync();
    }

    // ── Connection timeout ────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectAsync_NoServer_ThrowsException()
    {
        var client = new NamedPipeClientTransport("piston-nonexistent-pipe-xyz");
        var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<Exception>(
            () => client.ConnectAsync(cts.Token));

        await client.DisposeAsync();
    }

    // ── NamedPipeListener: two clients connect sequentially ──────────────────

    [Fact]
    public async Task Listener_TwoClientsConnect_EachGetsOwnStream()
    {
        var pipeName = UniquePipeName();
        var listener = new NamedPipeListener(pipeName);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var streams = new List<Stream>();

        // Accept in background
        var acceptTask = Task.Run(async () =>
        {
            await foreach (var stream in listener.AcceptClientsAsync(cts.Token))
            {
                streams.Add(stream);
                if (streams.Count == 2)
                    break;
            }
        });

        // Connect two clients
        var client1 = new NamedPipeClientTransport(pipeName);
        await client1.ConnectAsync(cts.Token);

        var client2 = new NamedPipeClientTransport(pipeName);
        await client2.ConnectAsync(cts.Token);

        await acceptTask;

        Assert.Equal(2, streams.Count);
        Assert.NotSame(streams[0], streams[1]);

        foreach (var s in streams) s.Dispose();
        await client1.DisposeAsync();
        await client2.DisposeAsync();
        await listener.DisposeAsync();
    }

    // ── GeneratePipeName is deterministic ─────────────────────────────────────

    [Fact]
    public void GeneratePipeName_SamePath_ReturnsSameResult()
    {
        var name1 = NamedPipeListener.GeneratePipeName("/repo/my-solution.slnx");
        var name2 = NamedPipeListener.GeneratePipeName("/repo/my-solution.slnx");

        Assert.Equal(name1, name2);
        Assert.StartsWith("piston-", name1);
        Assert.Equal(15, name1.Length); // "piston-" + 8 hex chars
    }

    [Fact]
    public void GeneratePipeName_DifferentPaths_ReturnsDifferentNames()
    {
        var name1 = NamedPipeListener.GeneratePipeName("/repo/solution-a.slnx");
        var name2 = NamedPipeListener.GeneratePipeName("/repo/solution-b.slnx");

        Assert.NotEqual(name1, name2);
    }
}
