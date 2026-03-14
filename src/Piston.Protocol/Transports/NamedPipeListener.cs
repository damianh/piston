using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

namespace Piston.Protocol.Transports;

/// <summary>
/// Continuously accepts new client connections on a named pipe,
/// yielding a connected <see cref="Stream"/> for each client.
/// </summary>
public sealed class NamedPipeListener : IAsyncDisposable
{
    private readonly string _pipeName;
    private bool _disposed;

    public NamedPipeListener(string pipeName)
    {
        _pipeName = pipeName;
    }

    /// <summary>
    /// Yields a connected <see cref="Stream"/> for each new client.
    /// Cancelling <paramref name="ct"/> ends the accept loop.
    /// </summary>
    public async IAsyncEnumerable<Stream> AcceptClientsAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            var transport = new NamedPipeServerTransport(_pipeName);
            Stream stream;
            try
            {
                stream = await transport.AcceptClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                yield break;
            }
            catch (Exception)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            yield return stream;
        }
    }

    /// <summary>
    /// Computes a deterministic pipe name from a solution path.
    /// Format: <c>piston-{8-char-hex}</c>.
    /// </summary>
    public static string GeneratePipeName(string solutionPath)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(solutionPath));
        var hex   = Convert.ToHexString(bytes)[..8].ToLowerInvariant();
        return $"piston-{hex}";
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
