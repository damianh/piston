using System.IO.Pipes;
using Piston.Protocol.JsonRpc;

namespace Piston.Protocol.Transports;

/// <summary>
/// Client-side <see cref="IProtocolTransport"/> over a named pipe.
/// </summary>
public sealed class NamedPipeClientTransport : IProtocolTransport
{
    private readonly string _pipeName;
    private readonly string _serverName;
    private NamedPipeClientStream? _pipe;

    public NamedPipeClientTransport(string pipeName, string serverName = ".")
    {
        _pipeName   = pipeName;
        _serverName = serverName;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _pipe = new NamedPipeClientStream(
            _serverName,
            _pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        // Pass CancellationToken.None to ConnectAsync to avoid a Windows named-pipe
        // behaviour where a CT registered on the pipe's async-IO infrastructure can cancel
        // subsequent IO operations (WriteAsync/ReadAsync) after the same token fires.
        // Use WaitAsync to honour the caller's CT for the wait itself.
        await _pipe.ConnectAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
            .WaitAsync(ct)
            .ConfigureAwait(false);
    }

    public Task SendAsync(ReadOnlyMemory<byte> message, CancellationToken ct)
    {
        EnsureConnected();
        return MessageFramer.WriteMessageAsync(_pipe!, message, ct);
    }

    public async Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken ct)
    {
        EnsureConnected();
        var message = await MessageFramer.ReadMessageAsync(_pipe!, ct).ConfigureAwait(false);
        if (message is null)
            throw new IOException("Named pipe was closed by the remote end.");
        return message.Value;
    }

    public ValueTask DisposeAsync()
    {
        _pipe?.Dispose();
        return ValueTask.CompletedTask;
    }

    private void EnsureConnected()
    {
        if (_pipe is null || !_pipe.IsConnected)
            throw new InvalidOperationException("Transport is not connected. Call ConnectAsync first.");
    }
}
