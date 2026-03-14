using System.IO.Pipes;

namespace Piston.Protocol.Transports;

/// <summary>
/// Server-side named pipe transport that accepts a single client connection.
/// Create a new instance per client; the underlying <see cref="NamedPipeServerStream"/>
/// supports exactly one connection.
/// </summary>
public sealed class NamedPipeServerTransport : IAsyncDisposable
{
    private readonly string _pipeName;
    private NamedPipeServerStream? _pipe;

    public NamedPipeServerTransport(string pipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Waits for one client to connect, then returns the connected stream.
    /// </summary>
    public async Task<Stream> AcceptClientAsync(CancellationToken ct)
    {
        _pipe = new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous);

        // Pass CancellationToken.None to WaitForConnectionAsync to avoid a Windows named-pipe
        // behaviour where a CT registered on the pipe's async-IO infrastructure can cancel
        // subsequent IO operations (WriteAsync/ReadAsync) after the same token fires.
        // Use WaitAsync to honour the caller's CT for the wait itself.
        await _pipe.WaitForConnectionAsync(CancellationToken.None)
            .WaitAsync(ct)
            .ConfigureAwait(false);
        return _pipe;
    }

    public ValueTask DisposeAsync()
    {
        _pipe?.Dispose();
        return ValueTask.CompletedTask;
    }
}
