namespace Piston.Protocol;

public interface IProtocolTransport : IAsyncDisposable
{
    Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct);
    Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct);
    Task ConnectAsync(CancellationToken ct);
}
