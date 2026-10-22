// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Keeps one Microsoft.Testing.Platform host per test app alive in server mode, so repeated discoveries
/// and runs skip process start-up and initialization. A host is replaced when its app's output directory
/// changes (a rebuild), when it exits, and after a failed, timed-out or cancelled request (cancellation
/// kills it). A host serves one request at a time: a request for a busy host runs in a cold process.
/// Hosts get <c>PISTON_WARM_HOST=1</c>.
/// </summary>
internal sealed class MtpWarmHostPool : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WarmHost> _hosts = new(StringComparer.Ordinal);
    private readonly MtpServerSession.StageTimeouts _timeouts;
    private bool _disposed;

    public MtpWarmHostPool(MtpServerSession.StageTimeouts? timeouts = null) =>
        _timeouts = timeouts ?? MtpServerSession.StageTimeouts.Default;

    /// <summary>The number of live hosts.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _hosts.Count;
            }
        }
    }

    /// <summary>As <see cref="MtpServerSession.RunAsync"/>, on a warm host for <paramref name="appPath"/>.</summary>
    public async Task<MtpServerSession.Result> RunAsync(
        string appPath,
        string workingDirectory,
        string method,
        IReadOnlyList<JsonElement>? tests,
        Action<IReadOnlyList<JsonElement>> onNodes,
        CancellationToken ct,
        ExecutionLog? runLog = null)
    {
        var log = MtpServerSession.ResolveLog(runLog);
        var key = Path.GetFullPath(appPath);
        var fingerprint = OutputFingerprint(key);

        WarmHost? host;
        WarmHost? stale = null;
        var reused = false;
        lock (_gate)
        {
            if (_disposed)
            {
                host = null;
            }
            else if (_hosts.TryGetValue(key, out host) && host.Busy)
            {
                host = null;
                log?.Invoke("MtpServer", "Warm host busy; using a new process for this request");
            }
            else
            {
                if (host is not null
                    && (host.Fingerprint != fingerprint
                        || !string.Equals(host.WorkingDirectory, workingDirectory, StringComparison.Ordinal)
                        || host.HasExited))
                {
                    stale = host;
                    _ = _hosts.Remove(key);
                    host = null;
                }

                if (host is null)
                {
                    host = new WarmHost(key, workingDirectory, fingerprint);
                    _hosts[key] = host;
                }
                else
                {
                    reused = true;
                }

                host.Busy = true;
            }
        }

        if (stale is not null)
        {
            log?.Invoke("MtpServer", stale.HasExited ? "Warm host exited; starting another" : "Test app changed; replacing its warm host");
            await stale.DisposeAsync().ConfigureAwait(false);
        }

        if (host is null)
        {
            return await MtpServerSession.RunColdAsync(appPath, workingDirectory, method, tests, onNodes, ct, runLog).ConfigureAwait(false);
        }

        host.Output.Reset(log);
        if (!reused)
        {
            var started = await host.StartAsync(appPath, method, _timeouts, ct).ConfigureAwait(false);
            if (started is not null)
            {
                Retire(host);
                await host.DisposeAsync().ConfigureAwait(false);
                return started;
            }
        }
        else
        {
            log?.Invoke("MtpServer", $"{method}: warm host (pid {host.ProcessId.ToString(CultureInfo.InvariantCulture)}, request {host.Requests.ToString(CultureInfo.InvariantCulture)})");
        }

        MtpServerSession.Outcome outcome;
        string? error;
        try
        {
            (outcome, error) = await host.Connection!.RequestAsync(method, tests, onNodes, _timeouts, ct).ConfigureAwait(false);
        }
        catch
        {
            // A callback threw: the host is mid-request, so it cannot be reused.
            Retire(host);
            _ = await host.EndAsync(MtpServerSession.Outcome.Cancelled, null, log).ConfigureAwait(false);
            throw;
        }

        host.Requests++;
        if (error is not null)
        {
            log?.Invoke("MtpServer", $"Session {outcome}: {error}");
        }

        if (outcome == MtpServerSession.Outcome.Completed)
        {
            var stderr = host.Output.Snapshot();
            host.Output.Reset(null);
            bool poolDisposed;
            lock (_gate)
            {
                host.Busy = false;
                poolDisposed = _disposed;
            }

            if (poolDisposed)
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }

            return new MtpServerSession.Result(outcome, error, stderr);
        }

        Retire(host);
        var result = await host.EndAsync(outcome, error, log).ConfigureAwait(false);

        // A reused host may have died while idle: the request never reached it, so start afresh.
        if (reused && !host.Connection.ReportedNodes && !ct.IsCancellationRequested
            && outcome is MtpServerSession.Outcome.Failed or MtpServerSession.Outcome.NotStarted)
        {
            log?.Invoke("MtpServer", "Warm host failed before reporting any test; retrying in a new process");
            return await MtpServerSession.RunColdAsync(appPath, workingDirectory, method, tests, onNodes, ct, runLog).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// Identifies the build of the test app at <paramref name="appPath"/>: the name, size and write time of
    /// every file beside it. A rebuild, or instrumentation in place, changes it.
    /// </summary>
    internal static string OutputFingerprint(string appPath)
    {
        var directory = Path.GetDirectoryName(appPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return string.Empty;
        }

        var hash = new HashCode();
        var files = 0;
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            try
            {
                hash.Add(file.Name, StringComparer.Ordinal);
                hash.Add(file.Length);
                hash.Add(file.LastWriteTimeUtc.Ticks);
                files++;
            }
            catch (IOException)
            {
                // Deleted while enumerating: the directory is changing, so the next request rechecks it.
            }
        }

        return string.Create(CultureInfo.InvariantCulture, $"{files}:{hash.ToHashCode():x8}");
    }

    public async ValueTask DisposeAsync()
    {
        List<WarmHost> idle;
        lock (_gate)
        {
            _disposed = true;
            idle = [.. _hosts.Values.Where(h => !h.Busy)];
            _hosts.Clear();
        }

        // A busy host is disposed by its request when it ends.
        await Task.WhenAll(idle.Select(h => h.DisposeAsync().AsTask())).ConfigureAwait(false);
    }

    private void Retire(WarmHost host)
    {
        lock (_gate)
        {
            if (_hosts.TryGetValue(host.Key, out var current) && ReferenceEquals(current, host))
            {
                _ = _hosts.Remove(host.Key);
            }
        }
    }

    private sealed class WarmHost(string key, string workingDirectory, string fingerprint) : IAsyncDisposable
    {
        private OwnedTestProcess? _process;
        private TcpClient? _client;
        private bool _ended;

        public string Key { get; } = key;

        public string WorkingDirectory { get; } = workingDirectory;

        public string Fingerprint { get; } = fingerprint;

        public MtpServerSession.HostOutput Output { get; } = new(null);

        public MtpServerSession.Connection? Connection { get; private set; }

        /// <summary>Guarded by the pool's lock.</summary>
        public bool Busy { get; set; }

        public int Requests { get; set; }

        public int ProcessId => _process?.Process.Id ?? 0;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process is null || _process.Process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>Starts and initializes the host; returns the failure to report, or null once it is ready.</summary>
        public async Task<MtpServerSession.Result?> StartAsync(
            string appPath, string method, MtpServerSession.StageTimeouts timeouts, CancellationToken ct)
        {
            var (started, failure) = await MtpServerSession.StartHostAsync(appPath, WorkingDirectory, method, Output, warm: true, ct)
                .ConfigureAwait(false);
            if (started is null)
            {
                _ended = true;
                return failure;
            }

            _process = started.Process;
            _client = started.Client;
            Connection = new MtpServerSession.Connection(_client.GetStream());
            var (outcome, error) = await Connection.InitializeAsync(timeouts.Initialize, ct).ConfigureAwait(false);
            if (outcome == MtpServerSession.Outcome.Completed)
            {
                return null;
            }

            Output.Log?.Invoke("MtpServer", $"Session {outcome}: {error}");
            return await EndAsync(outcome, error, Output.Log).ConfigureAwait(false);
        }

        /// <summary>Ends the host after a request that left it unusable.</summary>
        public async Task<MtpServerSession.Result> EndAsync(
            MtpServerSession.Outcome outcome, string? error, Action<string, string>? log)
        {
            _ended = true;
            _client?.Dispose();
            var result = await MtpServerSession.EndAsync(_process!, outcome, error, Output, log).ConfigureAwait(false);
            Release();
            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_ended && _process is not null)
            {
                _ended = true;
                if (Connection is not null)
                {
                    using var exitDeadline = new CancellationTokenSource(MtpServerSession.ExitTimeout);
                    await Connection.TrySendExitAsync(exitDeadline.Token).ConfigureAwait(false);
                }

                _client?.Dispose();
                await MtpServerSession.WaitForExitOrKillAsync(_process, MtpServerSession.ExitTimeout, Output.Log).ConfigureAwait(false);
            }

            Release();
        }

        private void Release()
        {
            _client?.Dispose();
            _client = null;
            Connection?.Dispose();
            _process?.Dispose();
            _process = null;
        }
    }
}
