namespace Piston.Protocol.JsonRpc;

/// <summary>
/// Newline-delimited JSON (NDJSON) framing over a <see cref="Stream"/>.
/// Each message is a single UTF-8 JSON line terminated by <c>\n</c>.
/// </summary>
public static class MessageFramer
{
    private const byte Newline = (byte)'\n';

    /// <summary>
    /// Writes <paramref name="message"/> bytes followed by a newline to <paramref name="stream"/>.
    /// </summary>
    public static async Task WriteMessageAsync(
        Stream stream,
        ReadOnlyMemory<byte> message,
        CancellationToken ct)
    {
        await stream.WriteAsync(message, ct).ConfigureAwait(false);
        await stream.WriteAsync(new ReadOnlyMemory<byte>([Newline]), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one newline-terminated message from <paramref name="stream"/>.
    /// Returns <c>null</c> on EOF. Throws <see cref="InvalidOperationException"/>
    /// if the accumulated message exceeds <paramref name="maxMessageSize"/>.
    /// </summary>
    public static async Task<ReadOnlyMemory<byte>?> ReadMessageAsync(
        Stream stream,
        CancellationToken ct,
        int maxMessageSize = 4 * 1024 * 1024)
    {
        var buffer = new List<byte>(4096);
        var singleByte = new byte[1];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(singleByte, ct).ConfigureAwait(false);

            if (bytesRead == 0)
            {
                // EOF — return null to signal clean close; partial buffers are discarded
                return null;
            }

            var b = singleByte[0];

            if (b == Newline)
            {
                // End of message
                return buffer.ToArray();
            }

            buffer.Add(b);

            if (buffer.Count > maxMessageSize)
            {
                throw new InvalidOperationException(
                    $"Incoming message exceeded maximum size of {maxMessageSize} bytes. Connection will be closed.");
            }
        }
    }
}
