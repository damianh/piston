// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipes;
using System.Runtime.Versioning;

namespace Piston.Protocol.Transports;

/// <summary>
/// Server-side named pipe transport that accepts a single client connection.
/// Create a new instance per client; the underlying <see cref="NamedPipeServerStream"/>
/// supports exactly one connection.
/// Restricted to the current user via Windows ACL or Unix peer-credential checks.
/// </summary>
public sealed class NamedPipeServerTransport : IAsyncDisposable
{
    internal const PipeOptions UnixPipeOptions = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    private readonly string _pipeName;
    private NamedPipeServerStream? _pipe;

    public NamedPipeServerTransport(string pipeName) => _pipeName = pipeName;

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
        {
            return CreateServerStreamWindows();
        }

        return new NamedPipeServerStream(
            _pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
            transmissionMode: PipeTransmissionMode.Byte,
            options: UnixPipeOptions);
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
