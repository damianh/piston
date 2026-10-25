// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// One request against a Microsoft.Testing.Platform test app in server mode
/// (<c>&lt;app&gt; --server --client-host 127.0.0.1 --client-port N</c>): Piston listens, the app connects,
/// and they exchange LSP-framed JSON-RPC. Test node updates (<c>testing/testUpdates/tests</c>) stream
/// per test, including <c>in-progress</c> while a test runs. By default each discovery or run uses its own
/// process; <see cref="MtpWarmHostPool"/> keeps hosts alive to serve later requests over the same connection.
/// </summary>
/// <remarks>
/// A test app with a test host controller (for example Microsoft.Testing.Extensions.CodeCoverage under
/// <c>--coverage</c>) relaunches itself: the controller connects first as a passive node that only pushes
/// attachments (<c>multipleConnectionProvider</c>), then the child test host connects and serves requests.
/// See <see cref="ServerLink"/>.
/// </remarks>
internal static class MtpServerSession
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a host with a test host controller gets to exit after its request: the controller finishes
    /// its extensions' work (such as writing coverage reports) after the test host exits.
    /// </summary>
    internal static readonly TimeSpan ControllerExitTimeout = TimeSpan.FromMinutes(2);

    private const int MaxPassiveNodes = 4;
    private const int MaxStderrLines = 200;

    internal const string DiscoverMethod = "testing/discoverTests";
    internal const string RunMethod = "testing/runTests";

    /// <summary>Set to <c>1</c> in a host that Piston keeps alive for further requests.</summary>
    internal const string WarmHostVariable = "PISTON_WARM_HOST";

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

        /// <summary>
        /// The host stopped responding during initialization or discovery; no tests were run, so the
        /// caller can fall back.
        /// </summary>
        TimedOut,
    }

    internal sealed record Result(Outcome Outcome, string? Error, IReadOnlyList<string> Stderr);

    /// <summary>Deadlines for the session stages that precede test execution.</summary>
    internal sealed record StageTimeouts(TimeSpan Initialize, TimeSpan Discovery)
    {
        public static StageTimeouts Default { get; } = new(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    }

    /// <summary>
    /// Runs <paramref name="method"/> against the app at <paramref name="appPath"/> (a framework-dependent
    /// <c>.dll</c> or an executable), started in <paramref name="workingDirectory"/>. Each batch of updated
    /// nodes is passed to <paramref name="onNodes"/>; nodes are cloned and remain valid after the callback.
    /// Diagnostics and the host's console output go to <paramref name="runLog"/> when given, otherwise to the diagnostics log.
    /// With <paramref name="warmHosts"/>, the request is sent to a kept-alive host for the app when one is free.
    /// <paramref name="hostArguments"/> (such as coverage options) are appended to the host's command line; a
    /// request with them always runs in a host started for it alone.
    /// </summary>
    internal static Task<Result> RunAsync(
        string appPath,
        string workingDirectory,
        string method,
        IReadOnlyList<JsonElement>? tests,
        Action<IReadOnlyList<JsonElement>> onNodes,
        CancellationToken ct,
        ExecutionLog? runLog = null,
        MtpWarmHostPool? warmHosts = null,
        string? hostArguments = null) =>
        warmHosts is null || !string.IsNullOrWhiteSpace(hostArguments)
            ? RunColdAsync(appPath, workingDirectory, method, tests, onNodes, ct, runLog, hostArguments)
            : warmHosts.RunAsync(appPath, workingDirectory, method, tests, onNodes, ct, runLog);

    /// <summary>As <see cref="RunAsync"/>, in a host process started for this request alone.</summary>
    internal static async Task<Result> RunColdAsync(
        string appPath,
        string workingDirectory,
        string method,
        IReadOnlyList<JsonElement>? tests,
        Action<IReadOnlyList<JsonElement>> onNodes,
        CancellationToken ct,
        ExecutionLog? runLog = null,
        string? hostArguments = null)
    {
        var log = ResolveLog(runLog);
        var output = new HostOutput(log);
        var (host, failure) = await StartHostAsync(appPath, workingDirectory, method, output, warm: false, ct, hostArguments).ConfigureAwait(false);
        if (host is null)
        {
            return failure!;
        }

        using var processLifetime = host.Process;
        using var client = host.Client;
        using var listener = host.Listener;
        using var link = host.CreateLink(log);
        var processCleanedUp = false;
        try
        {
            var (outcome, error) = await ExchangeAsync(link, method, tests, onNodes, StageTimeouts.Default, ct).ConfigureAwait(false);
            if (error is not null)
            {
                log?.Invoke("MtpServer", $"Session {outcome}: {error}");
            }

            processCleanedUp = true;
            return await EndAsync(host.Process, outcome, error, output, log, link.ExitTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await KillAndWaitAsync(host.Process, log).ConfigureAwait(false);
            processCleanedUp = true;
            return new Result(Outcome.Cancelled, null, output.Snapshot());
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            log?.Invoke("MtpServer", $"Session failed: {ex.Message}");
            await WaitForExitOrKillAsync(host.Process, ExitTimeout, log).ConfigureAwait(false);
            processCleanedUp = true;
            return new Result(Outcome.NotStarted, $"Lost connection to the test host: {ex.Message}", output.Snapshot());
        }
        finally
        {
            if (!processCleanedUp)
            {
                await KillAndWaitAsync(host.Process, log).ConfigureAwait(false);
            }
        }
    }

    internal static Action<string, string>? ResolveLog(ExecutionLog? runLog) =>
        runLog is not null ? runLog.Write
            : DiagnosticLog.Instance is { } diagnostics ? diagnostics.Write : null;

    /// <summary>
    /// Ends a host whose request has finished with <paramref name="outcome"/>: a completed host is given
    /// time to exit, an unresponsive or unwanted one is killed, and a failed one is drained for its stderr.
    /// </summary>
    internal static async Task<Result> EndAsync(
        OwnedTestProcess process, Outcome outcome, string? error, HostOutput output, Action<string, string>? log,
        TimeSpan? exitTimeout = null, CancellationToken ct = default)
    {
        switch (outcome)
        {
            case Outcome.Completed:
                try
                {
                    await WaitForExitOrKillAsync(process, exitTimeout ?? ExitTimeout, log, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await KillAndWaitAsync(process, log).ConfigureAwait(false);
                    return new Result(Outcome.Cancelled, null, output.Snapshot());
                }

                break;
            case Outcome.Cancelled:
            case Outcome.TimedOut:
                // The host is unresponsive (or no longer wanted): there is nothing to wait for.
                await KillAndWaitAsync(process, log).ConfigureAwait(false);
                break;
            case Outcome.NotStarted:
            case Outcome.Failed:
            default:
                await WaitForExitOrKillAsync(process, ExitTimeout, log).ConfigureAwait(false);
                break;
        }

        return new Result(outcome, error, output.Snapshot());
    }

    /// <summary>
    /// A started host connected in server mode. The caller owns the process, the client and the listener,
    /// which stays open for the further connections of a host with a test host controller.
    /// </summary>
    internal sealed record StartedHost(OwnedTestProcess Process, TcpClient Client, TcpListener Listener)
    {
        /// <summary>A link over the first connection that accepts later ones on <see cref="Listener"/>.</summary>
        public ServerLink CreateLink(Action<string, string>? log) =>
            new(Client.GetStream(), async ct => await AcceptAsync(Listener, Process.Process, ct).ConfigureAwait(false) is { } next
                ? next.GetStream()
                : null, log);
    }

    /// <summary>
    /// Starts the host and waits for it to connect. On failure the process is cleaned up and the
    /// <see cref="Result"/> to report is returned instead.
    /// </summary>
    internal static async Task<(StartedHost? Host, Result? Failure)> StartHostAsync(
        string appPath, string workingDirectory, string method, HostOutput output, bool warm, CancellationToken ct,
        string? hostArguments = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var started = false;
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            listener.Dispose();
            return (null, new Result(Outcome.NotStarted, $"Could not listen for the test host: {ex.Message}", []));
        }

        try
        {
            var (host, failure) = await StartHostAsync(appPath, workingDirectory, method, output, warm, listener, hostArguments, ct)
                .ConfigureAwait(false);
            started = host is not null;
            return (host, failure);
        }
        finally
        {
            if (!started)
            {
                listener.Dispose();
            }
        }
    }

    private static async Task<(StartedHost? Host, Result? Failure)> StartHostAsync(
        string appPath, string workingDirectory, string method, HostOutput output, bool warm, TcpListener listener,
        string? hostArguments, CancellationToken ct)
    {
        var log = output.Log;
        var psi = CreateStartInfo(appPath, workingDirectory, ((IPEndPoint)listener.LocalEndpoint).Port, hostArguments, out var removedEnvironment);
        if (warm)
        {
            psi.Environment[WarmHostVariable] = "1";
        }

        log?.Invoke("MtpServer", $"{method}{(warm ? " (starting warm host)" : "")}: {psi.FileName} {psi.Arguments}");
        if (ChildProcessEnvironment.Describe(removedEnvironment) is { } environmentNote)
        {
            log?.Invoke("MtpServer", environmentNote);
        }

        OwnedTestProcess ownedProcess;
        try
        {
            ownedProcess = new OwnedTestProcess(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, new Result(Outcome.NotStarted, $"Could not start the test host: {ex.Message}", []));
        }

        var process = ownedProcess.Process;
        process.OutputDataReceived += (_, e) => output.Stdout(e.Data);
        process.ErrorDataReceived += (_, e) => output.Stderr(e.Data);

        try
        {
            ownedProcess.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ownedProcess.Dispose();
            return (null, new Result(Outcome.NotStarted, $"Could not start the test host: {ex.Message}", output.Snapshot()));
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            var client = await AcceptAsync(listener, process, ct).ConfigureAwait(false);
            if (client is not null)
            {
                return (new StartedHost(ownedProcess, client, listener), null);
            }

            await KillAndWaitAsync(ownedProcess, log).ConfigureAwait(false);
            ownedProcess.Dispose();
            return (null, new Result(Outcome.NotStarted, "The test host did not connect in server mode.", output.Snapshot()));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await KillAndWaitAsync(ownedProcess, log).ConfigureAwait(false);
            ownedProcess.Dispose();
            return (null, new Result(Outcome.Cancelled, null, output.Snapshot()));
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            log?.Invoke("MtpServer", $"Session failed: {ex.Message}");
            await WaitForExitOrKillAsync(ownedProcess, ExitTimeout, log).ConfigureAwait(false);
            ownedProcess.Dispose();
            return (null, new Result(Outcome.NotStarted, $"Lost connection to the test host: {ex.Message}", output.Snapshot()));
        }
    }

    /// <summary>
    /// Routes a host's console output to the log of the request it is serving, keeping that request's
    /// first stderr lines. A warm host serves several requests, so the target is switched per request.
    /// </summary>
    internal sealed class HostOutput(Action<string, string>? log)
    {
        private readonly Lock _gate = new();
        private Action<string, string>? _log = log;
        private List<string> _stderr = [];

        public Action<string, string>? Log
        {
            get
            {
                lock (_gate)
                {
                    return _log;
                }
            }
        }

        /// <summary>Starts a new request: output goes to <paramref name="log"/> and stderr is collected afresh.</summary>
        public void Reset(Action<string, string>? log)
        {
            lock (_gate)
            {
                _log = log;
                _stderr = [];
            }
        }

        public void Stdout(string? line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                Log?.Invoke("MtpServer:stdout", line);
            }
        }

        public void Stderr(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            Action<string, string>? log;
            lock (_gate)
            {
                log = _log;
                if (_stderr.Count < MaxStderrLines)
                {
                    _stderr.Add(line.Trim());
                }
            }

            log?.Invoke("MtpServer:stderr", line);
        }

        public IReadOnlyList<string> Snapshot()
        {
            lock (_gate)
            {
                return [.. _stderr];
            }
        }
    }

    /// <summary>
    /// The host process. It starts in <paramref name="workingDirectory"/> (the solution directory), as
    /// <c>dotnet test</c> starts it in the fallback, so relative paths resolve the same on either path.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string appPath, string workingDirectory, int port, string? hostArguments = null) =>
        CreateStartInfo(appPath, workingDirectory, port, hostArguments, out _);

    private static ProcessStartInfo CreateStartInfo(
        string appPath, string workingDirectory, int port, string? hostArguments, out IReadOnlyList<string> removedEnvironment)
    {
        var serverArgs = $"--server --client-host 127.0.0.1 --client-port {port.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrWhiteSpace(hostArguments))
        {
            serverArgs += " " + hostArguments.Trim();
        }

        var isDll = appPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(isDll ? "dotnet" : appPath, isDll ? $"exec \"{appPath}\" {serverArgs}" : serverArgs)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        removedEnvironment = ChildProcessEnvironment.Prepare(psi);

        // Piston may itself run inside a warm host (dogfooding): a cold host must not inherit the marker.
        _ = psi.Environment.Remove(WarmHostVariable);
        return psi;
    }

    /// <summary>
    /// Runs the JSON-RPC exchange over a connected <paramref name="stream"/>: initialize, then
    /// <paramref name="method"/>, then <c>exit</c>. Initialization and discovery are bounded by
    /// <paramref name="timeouts"/> (reported as <see cref="Outcome.TimedOut"/>, distinct from caller
    /// cancellation); test execution is not.
    /// </summary>
    internal static async Task<(Outcome Outcome, string? Error)> ExchangeAsync(
        Stream stream,
        string method,
        IReadOnlyList<JsonElement>? tests,
        Action<IReadOnlyList<JsonElement>> onNodes,
        StageTimeouts timeouts,
        CancellationToken ct,
        Func<CancellationToken, Task<Stream?>>? acceptNext = null)
    {
        using var link = new ServerLink(stream, acceptNext, log: null);
        return await ExchangeAsync(link, method, tests, onNodes, timeouts, ct).ConfigureAwait(false);
    }

    /// <summary>As <see cref="ExchangeAsync(Stream, string, IReadOnlyList{JsonElement}?, Action{IReadOnlyList{JsonElement}}, StageTimeouts, CancellationToken, Func{CancellationToken, Task{Stream?}}?)"/>, over <paramref name="link"/>.</summary>
    internal static async Task<(Outcome Outcome, string? Error)> ExchangeAsync(
        ServerLink link,
        string method,
        IReadOnlyList<JsonElement>? tests,
        Action<IReadOnlyList<JsonElement>> onNodes,
        StageTimeouts timeouts,
        CancellationToken ct)
    {
        var initialized = await link.OpenAsync(timeouts.Initialize, ct).ConfigureAwait(false);
        if (initialized.Outcome != Outcome.Completed)
        {
            return initialized;
        }

        var connection = link.Active!;
        var request = await connection.RequestAsync(method, tests, onNodes, timeouts, ct).ConfigureAwait(false);
        if (request.Outcome == Outcome.Completed)
        {
            await connection.TrySendExitAsync(ct).ConfigureAwait(false);
        }

        return request;
    }

    /// <summary>
    /// The connections to one host. The first connection is initialized; when it is a passive node
    /// (<c>multipleConnectionProvider</c>, as a test host controller connects), its pushed messages are drained
    /// in the background and the next connection, from the test host it launches, becomes <see cref="Active"/>.
    /// Passive connections stay open until the link is disposed: the controller may push attachments until it exits.
    /// </summary>
    internal sealed class ServerLink(Stream first, Func<CancellationToken, Task<Stream?>>? acceptNext, Action<string, string>? log)
        : IDisposable
    {
        private readonly List<Stream> _streams = [first];
        private readonly List<Connection> _connections = [];
        private Connection? _active;
        private Stream? _activeStream;

        /// <summary>The connection that serves requests, once <see cref="OpenAsync"/> has initialized it.</summary>
        public Connection? Active => _active?.Initialized == true ? _active : null;

        /// <summary>Whether the host connected passive nodes, i.e. runs under a test host controller.</summary>
        public bool HasPassiveNodes { get; private set; }

        /// <summary>How long the host gets to exit after a completed request.</summary>
        public TimeSpan ExitTimeout => HasPassiveNodes ? ControllerExitTimeout : MtpServerSession.ExitTimeout;

        /// <summary>
        /// Initializes the first connection and, while it is a passive node, the next. Bounded by
        /// <paramref name="initializeTimeout"/> per connection, and by the connect timeout while waiting for one.
        /// </summary>
        public async Task<(Outcome Outcome, string? Error)> OpenAsync(TimeSpan initializeTimeout, CancellationToken ct)
        {
            var stream = first;
            for (var passiveNodes = 0; ; passiveNodes++)
            {
                var connection = new Connection(stream);
                _connections.Add(connection);
                _active = connection;
                _activeStream = stream;
                var initialized = await connection.InitializeAsync(initializeTimeout, ct).ConfigureAwait(false);
                if (initialized.Outcome != Outcome.Completed || !connection.MultipleConnectionProvider)
                {
                    return initialized;
                }

                if (acceptNext is null || passiveNodes >= MaxPassiveNodes)
                {
                    return (Outcome.NotStarted, "The test host connected as a passive node, but no further connection could be accepted.");
                }

                HasPassiveNodes = true;
                log?.Invoke("MtpServer", "A test host controller connected as a passive node; waiting for its test host to connect");
                _ = DrainPassiveAsync(connection);

                Stream? next;
                try
                {
                    next = await acceptNext(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return (Outcome.Cancelled, null);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
                {
                    return (Outcome.NotStarted, $"Lost connection to the test host: {ex.Message}");
                }

                if (next is null)
                {
                    return (Outcome.NotStarted, "The test host did not connect after its test host controller.");
                }

                _streams.Add(next);
                stream = next;
            }
        }

        /// <summary>Closes the active connection, leaving passive nodes open until the link is disposed.</summary>
        public void CloseActive() => _activeStream?.Dispose();

        public void Dispose()
        {
            foreach (var stream in _streams)
            {
                stream.Dispose();
            }

            foreach (var connection in _connections)
            {
                connection.Dispose();
            }
        }

        private async Task DrainPassiveAsync(Connection connection)
        {
            try
            {
                await connection.DrainAsync(message =>
                {
                    if (message.TryGetProperty("method", out var method)
                        && method.GetString() == "testing/testUpdates/attachments"
                        && message.TryGetProperty("params", out var parameters)
                        && parameters.TryGetProperty("attachments", out var attachments)
                        && attachments.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var attachment in attachments.EnumerateArray())
                        {
                            if (attachment.TryGetProperty("uri", out var uri))
                            {
                                log?.Invoke("MtpServer", $"Attachment from the test host controller: {uri.GetString()}");
                            }
                        }
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or ObjectDisposedException or FormatException or OverflowException)
            {
                // The link is closing, or the controller went away.
            }
        }
    }

    /// <summary>
    /// A JSON-RPC connection to a host in server mode. After <see cref="InitializeAsync"/> it accepts any
    /// number of requests, one at a time.
    /// </summary>
    internal sealed class Connection(Stream stream) : IDisposable
    {
        private readonly FrameReader _reader = new(stream);
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private int _nextId;

        /// <summary>Whether the host accepted <c>initialize</c>.</summary>
        public bool Initialized { get; private set; }

        /// <summary>Whether the current (or last) request has reported any test node.</summary>
        public bool ReportedNodes { get; private set; }

        /// <summary>
        /// Whether the host answered <c>initialize</c> as a passive node (<c>multipleConnectionProvider</c>):
        /// it only pushes attachments, and requests go to another connection.
        /// </summary>
        public bool MultipleConnectionProvider { get; private set; }

        public Task<(Outcome Outcome, string? Error)> InitializeAsync(TimeSpan timeout, CancellationToken ct) =>
            GuardAsync("initialization", timeout, ct, async token =>
            {
                var id = ++_nextId;
                await SendAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    method = "initialize",
                    @params = new
                    {
                        processId = Environment.ProcessId,
                        clientInfo = new { name = "piston", version = "1.0" },
                        capabilities = new { testing = new { debuggerProvider = false } },
                    },
                }, token).ConfigureAwait(false);

                var (ended, error) = await ReadResponseAsync(id, onNodes: null, token, result =>
                    MultipleConnectionProvider = result.TryGetProperty("capabilities", out var capabilities)
                        && capabilities.ValueKind == JsonValueKind.Object
                        && capabilities.TryGetProperty("testing", out var testing)
                        && testing.ValueKind == JsonValueKind.Object
                        && testing.TryGetProperty("multipleConnectionProvider", out var multiple)
                        && multiple.ValueKind == JsonValueKind.True).ConfigureAwait(false);
                if (ended)
                {
                    return (Outcome.NotStarted, "The test host disconnected before initializing.");
                }

                if (error is not null)
                {
                    return (Outcome.NotStarted, $"The test host rejected initialization: {error}");
                }

                Initialized = true;
                return (Outcome.Completed, (string?)null);
            });

        /// <summary>
        /// Sends <paramref name="method"/> and reads until its response. Discovery is bounded by
        /// <see cref="StageTimeouts.Discovery"/>; test execution has no deadline, as tests may legitimately
        /// run for a long time.
        /// </summary>
        public Task<(Outcome Outcome, string? Error)> RequestAsync(
            string method,
            IReadOnlyList<JsonElement>? tests,
            Action<IReadOnlyList<JsonElement>> onNodes,
            StageTimeouts timeouts,
            CancellationToken ct)
        {
            ReportedNodes = false;
            var discovery = method == DiscoverMethod;
            return GuardAsync(discovery ? "discovery" : "execution", discovery ? timeouts.Discovery : Timeout.InfiniteTimeSpan, ct, async token =>
            {
                var id = ++_nextId;
                var requestParams = new JsonObject { ["runId"] = Guid.NewGuid().ToString() };
                if (tests is not null)
                {
                    requestParams["tests"] = new JsonArray([.. tests.Select(t => JsonNode.Parse(t.GetRawText()))]);
                }

                await SendAsync(new { jsonrpc = "2.0", id, method, @params = requestParams }, token).ConfigureAwait(false);

                // The terminal update (changes == null) precedes the request's response on the wire, so the
                // response marks the end of the request.
                var (ended, error) = await ReadResponseAsync(id, nodes =>
                {
                    ReportedNodes = true;
                    onNodes(nodes);
                }, token).ConfigureAwait(false);

                return ended
                    ? (Outcome.Failed, "The test host exited before the run completed.")
                    : (Outcome.Completed, error);
            });
        }

        /// <summary>Asks the host to exit, ignoring a host that has already gone.</summary>
        public async Task TrySendExitAsync(CancellationToken ct)
        {
            try
            {
                await SendAsync(new { jsonrpc = "2.0", method = "exit", @params = new { } }, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            {
            }
        }

        public void Dispose() => _writeLock.Dispose();

        private async Task<(Outcome Outcome, string? Error)> GuardAsync(
            string stageName,
            TimeSpan timeout,
            CancellationToken ct,
            Func<CancellationToken, Task<(Outcome Outcome, string? Error)>> stage)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                return await stage(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return (Outcome.Cancelled, null);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                return (Outcome.TimedOut,
                    $"The test host did not complete {stageName} within {timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s.");
            }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or ObjectDisposedException or FormatException or OverflowException)
            {
                return (Initialized ? Outcome.Failed : Outcome.NotStarted, $"Lost connection to the test host: {ex.Message}");
            }
        }

        private async Task SendAsync(object message, CancellationToken ct)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n");
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(header, ct).ConfigureAwait(false);
                await stream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            finally
            {
                _ = _writeLock.Release();
            }
        }

        /// <summary>
        /// Reads messages until the response to request <paramref name="id"/>, passing test node updates to
        /// <paramref name="onNodes"/> and a successful response's result to <paramref name="onResult"/>.
        /// Returns <c>Ended</c> when the stream closes first.
        /// </summary>
        private async Task<(bool Ended, string? Error)> ReadResponseAsync(
            int id, Action<IReadOnlyList<JsonElement>>? onNodes, CancellationToken ct, Action<JsonElement>? onResult = null)
        {
            while (true)
            {
                using var message = await _reader.ReadAsync(ct).ConfigureAwait(false);
                if (message is null)
                {
                    return (true, null);
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
                    }, ct).ConfigureAwait(false);
                    continue;
                }

                if (hasMethod)
                {
                    if (onNodes is not null
                        && methodElement.GetString() == "testing/testUpdates/tests"
                        && root.TryGetProperty("params", out var updateParams)
                        && updateParams.TryGetProperty("changes", out var changes)
                        && changes.ValueKind == JsonValueKind.Array)
                    {
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

                if (!hasId || idElement.ValueKind != JsonValueKind.Number || idElement.GetInt32() != id)
                {
                    continue;
                }

                if (root.TryGetProperty("error", out var errorElement))
                {
                    return (false, errorElement.TryGetProperty("message", out var msg) ? msg.GetString() : errorElement.GetRawText());
                }

                if (onResult is not null && root.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object)
                {
                    onResult(result);
                }

                return (false, null);
            }
        }

        /// <summary>
        /// Reads a passive node's pushed messages, passing each to <paramref name="onMessage"/>, until the
        /// stream closes.
        /// </summary>
        public async Task DrainAsync(Action<JsonElement> onMessage)
        {
            while (true)
            {
                using var message = await _reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
                if (message is null)
                {
                    return;
                }

                onMessage(message.RootElement);
            }
        }
    }

    private static async Task<TcpClient?> AcceptAsync(TcpListener listener, Process process, CancellationToken ct)
    {
        using var acceptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        acceptCts.CancelAfter(ConnectTimeout);
        var accept = listener.AcceptTcpClientAsync(acceptCts.Token).AsTask();
        var exited = RedirectedProcessDrain.WaitForProcessExitAsync(process, acceptCts.Token);
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

    internal static Task WaitForExitOrKillAsync(
        OwnedTestProcess process, TimeSpan timeout, Action<string, string>? log, CancellationToken ct = default) =>
        RedirectedProcessDrain.WaitForExitAndDrainAsync(
            process,
            RedirectedProcessDrain.DefaultTimeout,
            ct,
            message => log?.Invoke("MtpServer", message),
            timeout);

    internal static Task KillAndWaitAsync(OwnedTestProcess process, Action<string, string>? log) =>
        RedirectedProcessDrain.KillAndWaitAsync(
            process,
            CancellationToken.None,
            message => log?.Invoke("MtpServer", message));

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
