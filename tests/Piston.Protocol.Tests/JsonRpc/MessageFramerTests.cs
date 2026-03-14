using System.IO.Pipelines;
using System.Text;
using Piston.Protocol.JsonRpc;
using Xunit;

namespace Piston.Protocol.Tests.JsonRpc;

public sealed class MessageFramerTests
{
    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ── Single message round-trip ─────────────────────────────────────────────

    [Fact]
    public async Task WriteAndRead_SingleMessage_RoundTrips()
    {
        var stream  = new MemoryStream();
        var message = Utf8("{\"hello\":\"world\"}");

        await MessageFramer.WriteMessageAsync(stream, message, CancellationToken.None);
        stream.Seek(0, SeekOrigin.Begin);

        var result = await MessageFramer.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(message, result!.Value.ToArray());
    }

    // ── Three messages — order preserved ─────────────────────────────────────

    [Fact]
    public async Task WriteAndRead_ThreeMessages_OrderPreserved()
    {
        var stream   = new MemoryStream();
        var messages = new[]
        {
            Utf8("{\"seq\":1}"),
            Utf8("{\"seq\":2}"),
            Utf8("{\"seq\":3}"),
        };

        foreach (var m in messages)
            await MessageFramer.WriteMessageAsync(stream, m, CancellationToken.None);

        stream.Seek(0, SeekOrigin.Begin);

        for (var i = 0; i < 3; i++)
        {
            var result = await MessageFramer.ReadMessageAsync(stream, CancellationToken.None);
            Assert.NotNull(result);
            Assert.Equal(messages[i], result!.Value.ToArray());
        }
    }

    // ── EOF returns null ──────────────────────────────────────────────────────

    [Fact]
    public async Task ReadMessage_EmptyStream_ReturnsNull()
    {
        var stream = new MemoryStream();
        var result = await MessageFramer.ReadMessageAsync(stream, CancellationToken.None);

        Assert.Null(result);
    }

    // ── Large message round-trips ─────────────────────────────────────────────

    [Fact]
    public async Task WriteAndRead_LargeMessage_RoundTrips()
    {
        // 128 KB of JSON payload
        var payload = new string('x', 128 * 1024);
        var message = Utf8($"{{\"data\":\"{payload}\"}}");
        var stream  = new MemoryStream();

        await MessageFramer.WriteMessageAsync(stream, message, CancellationToken.None);
        stream.Seek(0, SeekOrigin.Begin);

        var result = await MessageFramer.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(message, result!.Value.ToArray());
    }

    // ── Unicode characters preserved ──────────────────────────────────────────

    [Fact]
    public async Task WriteAndRead_Unicode_Preserved()
    {
        var message = Utf8("{\"text\":\"日本語テスト 🎉\"}");
        var stream  = new MemoryStream();

        await MessageFramer.WriteMessageAsync(stream, message, CancellationToken.None);
        stream.Seek(0, SeekOrigin.Begin);

        var result = await MessageFramer.ReadMessageAsync(stream, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(message, result!.Value.ToArray());
    }

    // ── Max message size throws ───────────────────────────────────────────────

    [Fact]
    public async Task ReadMessage_ExceedsMaxSize_Throws()
    {
        // Write a message that exceeds the tiny limit
        var stream  = new MemoryStream();
        var message = Utf8(new string('a', 200));
        await MessageFramer.WriteMessageAsync(stream, message, CancellationToken.None);
        stream.Seek(0, SeekOrigin.Begin);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => MessageFramer.ReadMessageAsync(stream, CancellationToken.None, maxMessageSize: 100));
    }

    // ── Concurrent write + read via Pipe ─────────────────────────────────────

    [Fact]
    public async Task ConcurrentWriteRead_PipeStream_RoundTrips()
    {
        var pipe   = new System.IO.Pipelines.Pipe();
        var reader = pipe.Reader.AsStream();
        var writer = pipe.Writer.AsStream();

        var messages = Enumerable.Range(1, 5)
            .Select(i => Utf8($"{{\"index\":{i}}}"))
            .ToArray();

        // Write in background
        var writeTask = Task.Run(async () =>
        {
            foreach (var m in messages)
                await MessageFramer.WriteMessageAsync(writer, m, CancellationToken.None);
        });

        // Read concurrently
        var received = new List<byte[]>();
        for (var i = 0; i < messages.Length; i++)
        {
            var result = await MessageFramer.ReadMessageAsync(reader, CancellationToken.None);
            Assert.NotNull(result);
            received.Add(result!.Value.ToArray());
        }

        await writeTask;

        Assert.Equal(messages.Length, received.Count);
        for (var i = 0; i < messages.Length; i++)
            Assert.Equal(messages[i], received[i]);
    }
}
