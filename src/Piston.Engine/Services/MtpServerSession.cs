// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Impact;

namespace Piston.Engine.Services;

/// <summary>
/// One request against a Microsoft.Testing.Platform test app in server mode
/// (<c>&lt;app&gt; --server --client-host 127.0.0.1 --client-port N</c>): Piston listens, the app connects,
/// and they exchange LSP-framed JSON-RPC. Test node updates (<c>testing/testUpdates/tests</c>) stream
/// per test, including <c>in-progress</c> while a test runs. MTP does not support multiple requests per
/// process, so each discovery or run uses its own process.
/// </summary>
internal static class MtpServerSession
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);
    private const int MaxStderrLines = 200;

    internal const string DiscoverMethod = "testing/discoverTests";
    internal const string RunMethod = "testing/runTests";

    /// <summary>How a session ended.</summary>
    internal enum Outcome
    {
        /// <summary>The request completed (its response may still carry an error).</summary>
        Completed,

        /// <summary>The app never initialized; no tests were run.</summary>
        NotStarted,

        /// <summary>The app exited or the connection failed after initialization.</summary>
        Failed,

        /// <summary>Cancelled by the caller.</summary>
        Cancelled,
    }

    internal sealed record Result(Outcome Outcome, string? Error, IReadOnlyList<string> Stderr);

    /// <summary>
    /// Runs <paramref name="method"/> against the app at <paramref name="appPath"/> (a framework-dependent
    /// <c>.dll</c> or an executable). Each batch of updated nodes is passed to <paramref name="onNodes"/>;
    /// nodes are cloned and remain valid after the callback.
    /// </summary>
    internal static async Task<Result> RunAsync(
        string appPath,
        string method,
        IReadOnlyList<JsonElement>? tests,
        Action<IReadOnlyList<JsonElement>> onNodes,
        CancellationToken ct)
    {
        var log = DiagnosticLog.Instance;
        var stderr = new List<string>();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            return new Result(Outcome.NotStarted, $"Could not listen for the test host: {ex.Message}", stderr);
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverArgs = $"--server --client-host 127.0.0.1 --client-port {port.ToString(CultureInfo.InvariantCulture)}";
        var isDll = appPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isDll ? "dotnet" : appPath, isDll ? $"exec \"{appPath}\" {serverArgs}" : serverArgs)
        {
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);

        log?.Write("MtpServer", $"{method}: {psi.FileName} {psi.Arguments}");

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                log?.Write("MtpServer:stdout", e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data))
            {
                return;
            }

            log?.Write("MtpServer:stderr", e.Data);
            lock (stderr)
            {
                if (stderr.Count < MaxStderrLines)
                {
                    stderr.Add(e.Data.Trim());
                }
            }
        };

        try
        {
            _ = process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new Result(Outcome.NotStarted, $"Could not start the test host: {ex.Message}", stderr);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var initialized = false;
        try
        {
            using var client = await AcceptAsync(listener, process, ct).ConfigureAwait(false);
            if (client is null)
            {
                return new Result(Outcome.NotStarted, "The test host did not connect in server mode.", Snapshot(stderr));
            }

            var stream = client.GetStream();
            using var writeLock = new SemaphoreSlim(1, 1);

            async Task SendAsync(object message)
            {
                var body = JsonSerializer.SerializeToUtf8Bytes(message);
                var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
                await writeLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await stream.WriteAsync(header, ct).ConfigureAwait(false);
                    await stream.WriteAsync(body, ct).ConfigureAwait(false);
                }
                finally
                {
                    _ = writeLock.Release();
                }
            }

            await SendAsync(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    processId = Environment.ProcessId,
                    clientInfo = new { name = "piston", version = "1.0" },
                    capabilities = new { testing = new { debuggerProvider = false } },
                },
            }).ConfigureAwait(false);

            var reader = new FrameReader(stream);
            string? requestError = null;

            // The terminal update (changes == null) precedes the request's response on the wire, so the
            // response marks the end of the session.
            while (true)
            {
                using var message = await reader.ReadAsync(ct).ConfigureAwait(false);
                if (message is null)
                {
                    var error = initialized
                        ? "The test host exited before the run completed."
                        : "The test host disconnected before initializing.";
                    return new Result(initialized ? Outcome.Failed : Outcome.NotStarted, error, await StderrAfterExitAsync(process, stderr).ConfigureAwait(false));
                }

                var root = message.RootElement;
                var hasId = root.TryGetProperty("id", out var idElement);
                var hasMethod = root.TryGetProperty("method", out var methodElement);

                if (hasMethod && hasId)
                {
                    // Server-to-client requests (for example debugger attach) are not supported.
                    await SendAsync(new
                    {
                        jsonrpc = "2.0",
                        id = JsonNode.Parse(idElement.GetRawText()),
                        error = new { code = -32601, message = "Method not supported by Piston." },
                    }).ConfigureAwait(false);
                    continue;
                }

                if (hasMethod)
                {
                    if (methodElement.GetString() == "testing/testUpdates/tests"
                        && root.TryGetProperty("params", out var updateParams))
                    {
                        if (!updateParams.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        var nodes = new List<JsonElement>(changes.GetArrayLength());
                        foreach (var change in changes.EnumerateArray())
                        {
                            if (change.TryGetProperty("node", out var node))
                            {
                                nodes.Add(node.Clone());
                            }
                        }

                        if (nodes.Count > 0)
                        {
                            onNodes(nodes);
                        }
                    }

                    continue;
                }

                if (!hasId || idElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }

                var errorText = root.TryGetProperty("error", out var errorElement)
                    ? errorElement.TryGetProperty("message", out var msg) ? msg.GetString() : errorElement.GetRawText()
                    : null;

                if (idElement.GetInt32() == 1)
                {
                    if (errorText is not null)
                    {
                        return new Result(Outcome.NotStarted, $"The test host rejected initialization: {errorText}", Snapshot(stderr));
                    }

                    initialized = true;
                    var requestParams = new JsonObject { ["runId"] = Guid.NewGuid().ToString() };
                    if (tests is not null)
                    {
                        requestParams["tests"] = new JsonArray([.. tests.Select(t => JsonNode.Parse(t.GetRawText()))]);
                    }

                    await SendAsync(new { jsonrpc = "2.0", id = 2, method, @params = requestParams }).ConfigureAwait(false);
                }
                else if (idElement.GetInt32() == 2)
                {
                    requestError = errorText;
                    break;
                }
            }

            try
            {
                await SendAsync(new { jsonrpc = "2.0", method = "exit", @params = new { } }).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            await WaitForExitOrKillAsync(process, ExitTimeout).ConfigureAwait(false);
            return new Result(Outcome.Completed, requestError, Snapshot(stderr));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Kill(process);
            return new Result(Outcome.Cancelled, null, Snapshot(stderr));
        }
        catch (Exception ex) when (ex is IOException or SocketException or JsonException or ObjectDisposedException or FormatException or OverflowException)
        {
            log?.Write("MtpServer", $"Session failed: {ex.Message}");
            return new Result(initialized ? Outcome.Failed : Outcome.NotStarted,
                $"Lost connection to the test host: {ex.Message}",
                await StderrAfterExitAsync(process, stderr).ConfigureAwait(false));
        }
        finally
        {
            Kill(process);
        }
    }

    private static async Task<TcpClient?> AcceptAsync(TcpListener listener, Process process, CancellationToken ct)
    {
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        acceptCts.CancelAfter(ConnectTimeout);
        var accept = listener.AcceptTcpClientAsync(acceptCts.Token).AsTask();
        var exited = process.WaitForExitAsync(acceptCts.Token);
        _ = await Task.WhenAny(accept, exited).ConfigureAwait(false);
        if (accept.IsCompletedSuccessfully)
        {
            var client = accept.Result;
            client.NoDelay = true;
            return client;
        }

        await acceptCts.CancelAsync().ConfigureAwait(false);
        try
        {
            var late = await accept.ConfigureAwait(false);
            return late;
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private static async Task<IReadOnlyList<string>> StderrAfterExitAsync(Process process, List<string> stderr)
    {
        await WaitForExitOrKillAsync(process, ExitTimeout).ConfigureAwait(false);
        return Snapshot(stderr);
    }

    private static async Task WaitForExitOrKillAsync(Process process, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static IReadOnlyList<string> Snapshot(List<string> stderr)
    {
        lock (stderr)
        {
            return [.. stderr];
        }
    }

    /// <summary>Reads LSP-framed (<c>Content-Length</c>) JSON messages.</summary>
    private sealed class FrameReader(Stream stream)
    {
        private readonly byte[] _buffer = new byte[64 * 1024];
        private int _start;
        private int _end;

        /// <summary>Returns the next message, or <see langword="null"/> at end of stream.</summary>
        public async Task<JsonDocument?> ReadAsync(CancellationToken ct)
        {
            var contentLength = -1;
            while (true)
            {
                var line = await ReadHeaderLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return null;
                }

                if (line.Length == 0)
                {
                    if (contentLength >= 0)
                    {
                        break;
                    }

                    continue;
                }

                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    contentLength = int.Parse(line[(colon + 1)..].Trim(), CultureInfo.InvariantCulture);
                }
            }

            var body = new byte[contentLength];
            var read = 0;
            while (read < contentLength)
            {
                if (_start < _end)
                {
                    var n = Math.Min(_end - _start, contentLength - read);
                    Array.Copy(_buffer, _start, body, read, n);
                    _start += n;
                    read += n;
                    continue;
                }

                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }
            }

            return JsonDocument.Parse(body);
        }

        private async Task<string?> ReadHeaderLineAsync(CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                if (_start >= _end && !await FillAsync(ct).ConfigureAwait(false))
                {
                    return null;
                }

                var b = (char)_buffer[_start++];
                if (b == '\n')
                {
                    return sb.ToString().TrimEnd('\r');
                }

                _ = sb.Append(b);
            }
        }

        private async Task<bool> FillAsync(CancellationToken ct)
        {
            _start = 0;
            _end = await stream.ReadAsync(_buffer, ct).ConfigureAwait(false);
            return _end > 0;
        }
    }
}
