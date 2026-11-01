// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Transports;

/// <summary>
/// A duplex <see cref="Stream"/> that reads from one stream and writes to another.
/// Used to present stdin/stdout as a single bidirectional stream for <c>ClientSession</c>.
/// </summary>
public sealed class StdioDuplexStream(Stream readFrom, Stream writeTo) : Stream
{
    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => readFrom.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => readFrom.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => readFrom.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
        => writeTo.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => writeTo.WriteAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => writeTo.WriteAsync(buffer, ct);

    public override void Flush()
        => writeTo.Flush();

    public override Task FlushAsync(CancellationToken ct)
        => writeTo.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            readFrom.Dispose();
            writeTo.Dispose();
        }
        base.Dispose(disposing);
    }
}
