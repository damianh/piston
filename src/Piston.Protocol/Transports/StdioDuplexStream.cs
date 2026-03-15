namespace Piston.Protocol.Transports;

/// <summary>
/// A duplex <see cref="Stream"/> that reads from one stream and writes to another.
/// Used to present stdin/stdout as a single bidirectional stream for <c>ClientSession</c>.
/// </summary>
public sealed class StdioDuplexStream : Stream
{
    private readonly Stream _readFrom;
    private readonly Stream _writeTo;

    public StdioDuplexStream(Stream readFrom, Stream writeTo)
    {
        _readFrom = readFrom;
        _writeTo  = writeTo;
    }

    public override bool CanRead  => true;
    public override bool CanWrite => true;
    public override bool CanSeek  => false;

    public override long Length   => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _readFrom.Read(buffer, offset, count);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _readFrom.ReadAsync(buffer, offset, count, ct);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        => _readFrom.ReadAsync(buffer, ct);

    public override void Write(byte[] buffer, int offset, int count)
        => _writeTo.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _writeTo.WriteAsync(buffer, offset, count, ct);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        => _writeTo.WriteAsync(buffer, ct);

    public override void Flush()
        => _writeTo.Flush();

    public override Task FlushAsync(CancellationToken ct)
        => _writeTo.FlushAsync(ct);

    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException();

    public override void SetLength(long value)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _readFrom.Dispose();
            _writeTo.Dispose();
        }
        base.Dispose(disposing);
    }
}
