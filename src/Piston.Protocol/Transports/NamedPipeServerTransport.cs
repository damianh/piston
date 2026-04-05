using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Piston.Protocol.Transports;

/// <summary>
/// Server-side named pipe transport that accepts a single client connection.
/// Create a new instance per client; the underlying <see cref="NamedPipeServerStream"/>
/// supports exactly one connection.
/// On Windows, the pipe is restricted to the current user via ACL (see <see cref="PipeSecurityHelper"/>).
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
        _pipe = CreateServerStream();

        // Pass CancellationToken.None to WaitForConnectionAsync to avoid a Windows named-pipe
        // behaviour where a CT registered on the pipe's async-IO infrastructure can cancel
        // subsequent IO operations (WriteAsync/ReadAsync) after the same token fires.
        // Use WaitAsync to honour the caller's CT for the wait itself.
        await _pipe.WaitForConnectionAsync(CancellationToken.None)
            .WaitAsync(ct)
            .ConfigureAwait(false);
        return _pipe;
    }

    private NamedPipeServerStream CreateServerStream()
    {
        if (OperatingSystem.IsWindows())
            return CreateServerStreamWindows();

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous);
    }

    [SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateServerStreamWindows()
    {
        var security = PipeSecurityHelper.CreateCurrentUserOnly();

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    public ValueTask DisposeAsync()
    {
        _pipe?.Dispose();
        return ValueTask.CompletedTask;
    }
}
