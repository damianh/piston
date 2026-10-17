// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Loopback listener for the <c>PistonLive</c> VSTest data collector (see <c>Piston.LiveCollector</c>).
/// Receives one line per test start/end and reports it via the callback. Accepts multiple connections,
/// since a multi-targeted project starts one collector per target framework.
/// </summary>
internal sealed class LiveTestEventListener : IAsyncDisposable
{
    internal const string PortVariable = "PISTON_LIVE_PORT";
    internal const string CollectorAssemblyName = "Piston.LiveCollector.dll";
    internal const string CollectorFriendlyName = "PistonLive";

    private readonly TcpListener _listener;
    private readonly Action<LiveTestEvent> _onEvent;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _readers = [];
    private readonly Task _acceptLoop;
    private int _disposed;

    private LiveTestEventListener(TcpListener listener, Action<LiveTestEvent> onEvent)
    {
        _listener = listener;
        _onEvent = onEvent;
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>The directory holding the collector, or <see langword="null"/> when it is not deployed.</summary>
    internal static string? CollectorDirectory
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "collectors");
            return File.Exists(Path.Combine(dir, CollectorAssemblyName)) ? dir : null;
        }
    }

    /// <summary>Starts listening on an ephemeral loopback port; returns <see langword="null"/> on failure.</summary>
    internal static LiveTestEventListener? TryStart(Action<LiveTestEvent> onEvent)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return new LiveTestEventListener(listener, onEvent);
        }
        catch (SocketException ex)
        {
            DiagnosticLog.Instance?.Write("VSTestRunner", $"Live test events unavailable: {ex.Message}");
            listener.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Parses one collector line: <c>S\t&lt;name&gt;[\t&lt;fqn&gt;]</c> or
    /// <c>E\t&lt;name&gt;\t&lt;outcome&gt;[\t&lt;fqn&gt;]</c>, where name is the test case's display name.
    /// </summary>
    internal static LiveTestEvent? Parse(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            return null;
        }

        string? FullyQualifiedName(int index) => parts.Length > index && parts[index].Length > 0 ? parts[index] : null;

        return parts[0] switch
        {
            "S" => new LiveTestEvent(parts[1], TestStatus.Running, FullyQualifiedName(2)),
            "E" => new LiveTestEvent(parts[1], parts.Length > 2 ? ParseOutcome(parts[2]) : TestStatus.NotRun, FullyQualifiedName(3)),
            _ => null,
        };
    }

    private static TestStatus ParseOutcome(string outcome) => outcome switch
    {
        "Passed" => TestStatus.Passed,
        "Failed" => TestStatus.Failed,
        "Skipped" => TestStatus.Skipped,
        _ => TestStatus.NotRun,
    };

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                lock (_readers)
                {
                    _readers.Add(ReadAsync(client));
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException
            || (ex is InvalidOperationException && Volatile.Read(ref _disposed) != 0))
        {
            // Accepting on a listener that disposal has already stopped is a normal shutdown.
        }
    }

    private async Task ReadAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.UTF8);
                while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
                {
                    if (Parse(line) is { } evt)
                    {
                        _onEvent(evt);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>
    /// Stops listening after giving open connections a moment to deliver buffered events (the test
    /// process has exited by the time this is called, so connections close promptly).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _listener.Stop();
        Task[] readers;
        lock (_readers)
        {
            readers = [.. _readers];
        }

        _ = await Task.WhenAny(Task.WhenAll(readers), Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
        await _cts.CancelAsync().ConfigureAwait(false);
        await _acceptLoop.ConfigureAwait(false);
        await Task.WhenAll(readers).ConfigureAwait(false);
        _cts.Dispose();
        _listener.Dispose();
    }
}

/// <summary>
/// A live test event: <see cref="TestStatus.Running"/> on start, or the outcome on end. The test case's
/// <paramref name="FullyQualifiedName"/>, when known, qualifies the display name into its <see cref="Identity"/>.
/// </summary>
internal sealed record LiveTestEvent(string DisplayName, TestStatus Status, string? FullyQualifiedName = null)
{
    /// <summary>The test's identity, matching the one <see cref="TrxResultParser"/> gives its final result.</summary>
    public string Identity => FullyQualifiedName is null ? DisplayName : TestIdentity.Of(FullyQualifiedName, DisplayName);
}
