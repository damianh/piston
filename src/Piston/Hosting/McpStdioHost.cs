// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Hosting.Configuration;
using Piston.Hosting.Protocol;
using Piston.Hosting.Services;
using Piston.Mcp;

namespace Piston.Hosting;

/// <summary>
/// <c>piston mcp</c>: one in-process engine per agent session, serving the Piston MCP tools over stdio.
/// Runs like the daemon (startup run, then watcher-triggered runs) and exits on stdin EOF, SIGTERM,
/// parent death or worktree deletion, cancelling runs, killing build/test processes and releasing the
/// worktree ownership lock. When the worktree is owned elsewhere it starts no engine and every tool fails
/// with an actionable message.
/// </summary>
public sealed class McpStdioHost(McpStdioHostOptions options)
{
    public const string OwnerHost = "mcp";
    private readonly Action<string> _log = options.Log;
    private DashboardHost? _dashboard;
    private ProtocolRouter? _router;
    private readonly CancellationTokenSource _dashboardLifetime = new();
    private Task _routerTask = Task.CompletedTask;
    private McpCallTracker? _callTracker;

    public EngineAvailability Availability { get; } = new();

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PistonEngine? engine = null;
        WorktreePresenceMonitor? monitor = null;
        try
        {
            engine = await StartEngineAsync().ConfigureAwait(false);
            if (engine is not null)
            {
                monitor = StartPresenceMonitor(engine, shutdown);
            }

            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
            {
                ContentRootPath = AppContext.BaseDirectory,
            });
            _ = builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
            _ = builder.Logging
                .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)
                .SetMinimumLevel(LogLevel.Warning);
            _ = builder.Services.AddSingleton<IEngine>(engine ?? (IEngine)new UnavailableEngine());
            _ = builder.Services.AddPistonMcpTools(_callTracker, availability: Availability)
                .WithStreamServerTransport(
                    options.Input ?? Console.OpenStandardInput(),
                    options.Output ?? Console.OpenStandardOutput());

            using var host = builder.Build();
            var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();
            using var stoppingRegistration = lifetime.ApplicationStopping.Register(() => shutdown.TrySetResult());
            using var cancelRegistration = ct.Register(() => shutdown.TrySetResult());
            using var parentWatchCts = new CancellationTokenSource();
            var parentWatch = options.MonitorParentProcess
                ? WatchParentAsync(shutdown, parentWatchCts.Token)
                : Task.CompletedTask;

            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
            _log("[piston] MCP server listening on stdio.");

            await shutdown.Task.ConfigureAwait(false);
            _log("[piston] Shutting down.");
            Availability.SetStopped();
            await parentWatchCts.CancelAsync().ConfigureAwait(false);
            await parentWatch.ConfigureAwait(false);

            using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await host.StopAsync(stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _log("[piston] MCP transport did not stop within 5s.");
            }
        }
        finally
        {
            monitor?.Dispose();
            await StopDashboardAsync(engine).ConfigureAwait(false);
            _dashboardLifetime.Dispose();
            if (engine is not null)
            {
                // Cancels runs (killing build/test process trees), drains them, then releases ownership.
                engine.Stop();
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        }

        _log("[piston] Stopped.");
        return 0;
    }

    private async Task<PistonEngine?> StartEngineAsync()
    {
        if (options.StartupError is { } startupError)
        {
            Availability.SetFaulted(startupError);
            _log($"[piston] {Availability.Reason}");
            return null;
        }

        var solutionPath = options.SolutionPath
            ?? throw new InvalidOperationException("SolutionPath is required without a StartupError.");
        var engineOptions = options.EngineOptions
            ?? throw new InvalidOperationException("EngineOptions is required without a StartupError.");

        PistonEngine? engine = null;
        try
        {
            var activity = new ActivityEventSinkProxy();
            engine = new PistonEngine(engineOptions, activity);
            _log($"[piston] Starting engine for: {solutionPath}");
            var startingEngine = engine;
            await engine.StartAsync(solutionPath, options.Dashboard
                ? () => StartDashboardAsync(startingEngine, activity)
                : null).ConfigureAwait(false);
            return engine;
        }
        catch (WorktreeOwnershipConflictException ex)
        {
            Availability.SetOwnedElsewhere(ex.Worktree, ex.Owner);
        }
        catch (Exception ex)
        {
            Availability.SetFaulted($"{ex.GetType().Name}: {ex.Message}");
        }

        _log($"[piston] {Availability.Reason}");
        if (engine is not null)
        {
            await StopDashboardAsync(engine).ConfigureAwait(false);
            await engine.DisposeAsync().ConfigureAwait(false);
        }
        return null;
    }

    private async Task StartDashboardAsync(PistonEngine engine, ActivityEventSinkProxy activity)
    {
        _router = new ProtocolRouter(engine);
        _router.SetMachineOverviewProvider(
            ct => MachineExecutionOverviewReader.ReadAsync(engine.ExecutionCoordinationDirectory, ct));
        activity.SetSink(_router);
        _callTracker = new McpCallTracker(_router.Emit, options.SolutionPath!);
        _router.SetMcpCallTracker(_callTracker);
        try
        {
            if (UserSettingsStore.ResolveDefaultPath() is { } path)
            {
                _router.SetUserSettingsStore(new UserSettingsStore(path));
            }
            else
            {
                _log("[piston] No application-data folder; machine-wide settings are unavailable.");
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _log($"[piston] Machine-wide settings are unavailable: {ex.Message}");
        }
        _routerTask = _router.RunAsync(_dashboardLifetime.Token);
        _dashboard = new DashboardHost();
        await _dashboard.StartAsync(_router, "http://127.0.0.1:0", () => engine.OwnerInfo, _dashboardLifetime.Token).ConfigureAwait(false);
        engine.PublishDashboardUrl(_dashboard.Url);
        _log($"[piston] Dashboard: {_dashboard.Url}");
    }

    private async Task StopDashboardAsync(PistonEngine? engine)
    {
        if (_dashboard is not null)
        {
            try
            {
                engine?.PublishDashboardUrl(null);
            }
            catch (ExecutionCoordinationException ex)
            {
                _log($"[piston] Could not withdraw dashboard endpoint: {ex.Message}");
            }
            await _dashboard.DisposeAsync().ConfigureAwait(false);
            _dashboard = null;
        }
        await _dashboardLifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _routerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_dashboardLifetime.IsCancellationRequested) { }
        if (_router is not null)
        {
            await _router.DisposeAsync().ConfigureAwait(false);
            _router = null;
        }
    }

    private WorktreePresenceMonitor StartPresenceMonitor(PistonEngine engine, TaskCompletionSource shutdown)
    {
        var solutionPath = options.SolutionPath!;
        var worktreeRoot = WorktreeIdentity.FromSolutionPath(solutionPath).RootPath;
        var monitor = new WorktreePresenceMonitor(solutionPath, worktreeRoot, root =>
        {
            Availability.SetWorktreeDeleted(root);
            _log($"[piston] Worktree '{root}' no longer exists; stopping engine and exiting.");
            engine.Stop();
            _ = Task.Delay(options.DeletionGracePeriod).ContinueWith(
                _ => shutdown.TrySetResult(), TaskScheduler.Default);
        }, options.PresenceCheckInterval);
        monitor.Start();
        return monitor;
    }

    private async Task WatchParentAsync(TaskCompletionSource shutdown, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            // Windows closes the stdin pipe when the parent dies, which ends the transport.
            return;
        }

        int initialParent;
        try
        {
            initialParent = GetParentProcessId();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (GetParentProcessId() != initialParent)
                {
                    _log($"[piston] Parent process {initialParent} exited; shutting down.");
                    _ = shutdown.TrySetResult();
                    return;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentProcessId();

    /// <summary>Resolves for DI when no engine runs; tools never reach it because the availability gate fails first.</summary>
    private sealed class UnavailableEngine : IEngine
    {
        public PistonState State { get; } = new();

        public Task StartAsync(string solutionPath) => throw Unavailable();
        public Task ForceRunAsync() => throw Unavailable();
        public void Stop() { }
        public void SetFilter(string? filter) => throw Unavailable();
        public void ClearResults() => throw Unavailable();
        public Task ResetAsync() => throw Unavailable();
        public void Dispose() { }

        private static InvalidOperationException Unavailable() => new("No Piston engine is running in this process.");
    }
}
