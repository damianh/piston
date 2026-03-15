using Piston.Protocol.JsonRpc;
using Piston.Protocol.Transports;
using Xunit;

namespace Piston.Protocol.Tests.Transports;

public sealed class StdioDuplexStreamTests
{
    // ── DuplexStream routes reads to readFrom, writes to writeTo ─────────────

    [Fact]
    public async Task ReadAsync_ReadsFromReadFromStream()
    {
        var data        = "hello world"u8.ToArray();
        var readFrom    = new MemoryStream(data);
        var writeTo     = new MemoryStream();
        var duplex      = new StdioDuplexStream(readFrom, writeTo);
        var cts         = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var buffer      = new byte[data.Length];
        var bytesRead   = await duplex.ReadAsync(buffer, cts.Token);

        Assert.Equal(data.Length, bytesRead);
        Assert.Equal(data, buffer);
    }

    [Fact]
    public async Task WriteAsync_WritesToWriteToStream()
    {
        var readFrom = new MemoryStream();
        var writeTo  = new MemoryStream();
        var duplex   = new StdioDuplexStream(readFrom, writeTo);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var data = "test message"u8.ToArray();
        await duplex.WriteAsync(data, cts.Token);

        Assert.Equal(data, writeTo.ToArray());
    }

    // ── NDJSON framing round-trip over duplex stream ──────────────────────────

    [Fact]
    public async Task NdjsonRoundTrip_WriteThenRead_ReturnsOriginalMessage()
    {
        var message = "{\"jsonrpc\":\"2.0\",\"method\":\"test\",\"params\":null}"u8.ToArray();

        // Simulate: write to a MemoryStream (stdout side), then read it back.
        var buffer = new MemoryStream();
        await MessageFramer.WriteMessageAsync(buffer, message, CancellationToken.None);

        // Reset so the duplex reads from the beginning.
        buffer.Position = 0;
        var readFrom = buffer;
        var writeTo  = new MemoryStream();
        var duplex   = new StdioDuplexStream(readFrom, writeTo);
        var cts      = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var received = await MessageFramer.ReadMessageAsync(duplex, cts.Token);

        Assert.NotNull(received);
        Assert.Equal(message, received!.Value.ToArray());
    }

    [Fact]
    public async Task NdjsonRoundTrip_MultipleMessages_AllReceived()
    {
        var messages = new[]
        {
            "{\"jsonrpc\":\"2.0\",\"id\":\"1\",\"method\":\"ping\"}"u8.ToArray(),
            "{\"jsonrpc\":\"2.0\",\"id\":\"2\",\"method\":\"pong\"}"u8.ToArray(),
            "{\"jsonrpc\":\"2.0\",\"id\":\"3\",\"method\":\"done\"}"u8.ToArray(),
        };

        // Write all messages to a buffer (simulating stdout).
        var outputBuffer = new MemoryStream();
        foreach (var msg in messages)
            await MessageFramer.WriteMessageAsync(outputBuffer, msg, CancellationToken.None);

        // Read them back via a duplex stream.
        outputBuffer.Position = 0;
        var duplex = new StdioDuplexStream(outputBuffer, new MemoryStream());
        var cts    = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        for (var i = 0; i < messages.Length; i++)
        {
            var received = await MessageFramer.ReadMessageAsync(duplex, cts.Token);
            Assert.NotNull(received);
            Assert.Equal(messages[i], received!.Value.ToArray());
        }
    }

    // ── EOF on stdin terminates cleanly ──────────────────────────────────────

    [Fact]
    public async Task ReadAsync_EmptyStream_ReturnsNullFromMessageFramer()
    {
        var emptyStream = new MemoryStream(Array.Empty<byte>());
        var duplex      = new StdioDuplexStream(emptyStream, new MemoryStream());
        var cts         = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await MessageFramer.ReadMessageAsync(duplex, cts.Token);

        Assert.Null(result);
    }

    // ── CanRead and CanWrite are true ─────────────────────────────────────────

    [Fact]
    public void CanRead_IsTrue()
    {
        var duplex = new StdioDuplexStream(new MemoryStream(), new MemoryStream());
        Assert.True(duplex.CanRead);
    }

    [Fact]
    public void CanWrite_IsTrue()
    {
        var duplex = new StdioDuplexStream(new MemoryStream(), new MemoryStream());
        Assert.True(duplex.CanWrite);
    }

    [Fact]
    public void CanSeek_IsFalse()
    {
        var duplex = new StdioDuplexStream(new MemoryStream(), new MemoryStream());
        Assert.False(duplex.CanSeek);
    }
}
