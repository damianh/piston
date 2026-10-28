// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Hosting.Configuration;
using Piston.Hosting.Protocol;
using Piston.Hosting.Services;
using Piston.Mcp;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

namespace Piston.Hosting;

/// <summary>
/// Hosts the full Piston daemon composition in-process: engine,
/// protocol router (named pipe + WebSocket), web dashboard server, and optional MCP server.
/// Used by the CLI (<c>piston daemon</c>).
/// </summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private readonly DaemonHostOptions _options;
    private readonly Action<string> _log;

    private PistonEngine? _engine;
    private ProtocolRouter? _router;
    private WebApplication? _webApp;
    private WebApplication? _mcpApp;
    private WorktreePresenceMonitor? _presenceMonitor;
    private readonly TaskCompletionSource _shutdownRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _startedAt;
    private bool _disposed;

    public DaemonHost(DaemonHostOptions options)
    {
        _options = options;
        _log = options.Log;
    }

    /// <summary>The running engine. Available after <see cref="StartAsync"/>.</summary>
    public IEngine Engine => _engine ?? throw new InvalidOperationException("DaemonHost not started.");

    /// <summary>The web dashboard URL. Available after <see cref="StartAsync"/>.</summary>
    public string WebUrl => $"http://localhost:{_options.WebPort}";

    /// <summary>The HTTP MCP endpoint URL, or null when MCP is disabled.</summary>
    public string? McpUrl => _options.McpPort is { } port ? $"http://localhost:{port}" : null;

    /// <summary>Asks <see cref="WaitForShutdownAsync"/> to return so the host can stop. Does not block.</summary>
    public void RequestShutdown()
    {
        _log("[piston] Shutdown requested.");
        _ = _shutdownRequested.TrySetResult();
    }

    /// <summary>Describes this daemon for <c>daemon/info</c>.</summary>
    public DaemonInfoResponse GetInfo() => new(
        Environment.ProcessId,
        _options.SolutionPath,
        _options.PipeName,
        _startedAt,
        typeof(DaemonHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
        WebUrl,
        McpUrl);

    /// <summary>
    /// Starts the engine, protocol router, web server, and MCP server,
    /// then runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await StartAsync(ct).ConfigureAwait(false);
        await WaitForShutdownAsync(ct).ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Starts all daemon components without blocking.</summary>
    /// <remarks>
    /// On failure, components already started are stopped so the engine releases its worktree
    /// ownership even when the hosting process keeps running.
    /// </remarks>
    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await StartCoreAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch (Exception stopEx)
            {
                _log($"[piston] Cleanup after failed start also failed: {stopEx.Message}");
            }
            throw;
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        _startedAt = DateTimeOffset.UtcNow;

        // Use a proxy sink so the router (created after engine) can receive activity events.
        var activityProxy = new ActivityEventSinkProxy();
        _engine = new PistonEngine(_options.EngineOptions, activityProxy);

        _log($"[piston] Starting engine for: {_options.SolutionPath}");
        var engine = _engine;

        // Endpoints start after worktree ownership is held but before watching or the startup run,
        // so an endpoint conflict fails the daemon without starting a run.
        await engine.StartAsync(_options.SolutionPath, () => StartEndpointsAsync(engine, activityProxy, ct))
            .ConfigureAwait(false);

        // A daemon must not outlive its worktree: shut down so ports, pipe and ownership are released.
        var worktreeRoot = WorktreeIdentity.FromSolutionPath(_options.SolutionPath).RootPath;
        _presenceMonitor = new WorktreePresenceMonitor(_options.SolutionPath, worktreeRoot, root =>
        {
            _log($"[piston] Worktree '{root}' no longer exists; shutting down.");
            RequestShutdown();
        }, _options.PresenceCheckInterval);
        _presenceMonitor.Start();
    }

    private void ConfigureUserSettings(ProtocolRouter router)
    {
        try
        {
            if (UserSettingsStore.ResolveDefaultPath() is { } path)
            {
                router.SetUserSettingsStore(new UserSettingsStore(path));
                return;
            }

            _log("[piston] No application-data folder; machine-wide settings are unavailable.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _log($"[piston] Machine-wide settings are unavailable: {ex.Message}");
        }
    }

    private async Task StartEndpointsAsync(PistonEngine engine, ActivityEventSinkProxy activityProxy, CancellationToken ct)
    {
        _log($"[piston] Listening on pipe: {_options.PipeName}");

        var listener = new NamedPipeListener(_options.PipeName);
        _router = new ProtocolRouter(engine, listener);
        _router.SetMachineOverviewProvider(
            ct => MachineExecutionOverviewReader.ReadAsync(_engine!.ExecutionCoordinationDirectory, ct));

        // Connect the activity proxy to the router so engine events are broadcast to clients
        activityProxy.SetSink(_router);

        // Wire MCP call tracking into the activity timeline and call log.
        var mcpCallTracker = new McpCallTracker(_router.Emit, _options.SolutionPath);
        _router.SetMcpCallTracker(mcpCallTracker);
        _router.SetDaemonControl(new DaemonControl(GetInfo, RequestShutdown));
        ConfigureUserSettings(_router);

        RouterTask = _router.RunAsync(ct);

        // Always start the web server for WebSocket + static file serving
        var webBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = "Piston",
            // Set content root to the directory containing the host binary so that
            // UseStaticFiles() can find the wwwroot/ folder with Blazor WASM assets.
            ContentRootPath = AppContext.BaseDirectory,
        });

        _webApp = webBuilder.Build();
        _webApp.Urls.Add(WebUrl);
        _ = _webApp.UseWebSockets();
        _ = _webApp.UseRequestGuard(
            securityHeaders: true,
            ContentSecurityPolicy.Build(ContentSecurityPolicy.ReadIndex(_webApp.Environment.WebRootFileProvider)));

        // Serve Blazor WASM framework files (handles content negotiation for .br/.gz compressed
        // assets) and static files. UseBlazorFrameworkFiles must come before UseStaticFiles.
        _ = _webApp.UseBlazorFrameworkFiles();
        _ = _webApp.UseDefaultFiles();
        _ = _webApp.UseStaticFiles();
        _ = _webApp.UseRouting();

        var router = _router;
        var stopping = _webApp.Lifetime.ApplicationStopping;
        _ = _webApp.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            // End the session when the server stops; otherwise shutdown waits for the dashboard to disconnect.
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct, stopping);
            await router.AddWebSocketSessionAsync(webSocket, session.Token).ConfigureAwait(false);
        });

        // Fallback to index.html for Blazor SPA client-side routing
        _ = _webApp.MapFallbackToFile("index.html");

        await StartEndpointAsync(_webApp, "web dashboard", WebUrl, "--web-port", ct).ConfigureAwait(false);
        _log($"[piston] Web server (WebSocket) listening on port: {_options.WebPort}");

        if (_options.McpPort is not null)
        {
            // MCP always runs on its own dedicated app to avoid middleware conflicts
            var mcpBuilder = WebApplication.CreateBuilder();
            _ = mcpBuilder.Services.AddSingleton<IEngine>(engine);
            _ = mcpBuilder.Services.AddPistonMcp(mcpCallTracker);
            _mcpApp = mcpBuilder.Build();
            _ = _mcpApp.UseRequestGuard(securityHeaders: false);
            _ = _mcpApp.MapMcp();
            _mcpApp.Urls.Add($"http://localhost:{_options.McpPort}");

            await StartEndpointAsync(_mcpApp, "MCP server", McpUrl!, "--mcp-port (or mcpPort in .piston.json)", ct)
                .ConfigureAwait(false);
            _log($"[piston] MCP server listening on port: {_options.McpPort}");
        }
    }

    private static async Task StartEndpointAsync(
        WebApplication app, string name, string url, string option, CancellationToken ct)
    {
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new DaemonStartupException(
                $"Could not start the {name} on {url}: {ex.Message} " +
                $"Another process (possibly another Piston daemon) may be using the port; stop it or choose a different {option}.",
                ex);
        }
    }

    /// <summary>Task representing the protocol router loop. Available after <see cref="StartAsync"/>.</summary>
    public Task RouterTask { get; private set; } = Task.CompletedTask;

    /// <summary>Waits until cancellation or shutdown is requested, or the router loop ends.</summary>
    public async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            _ = await Task.WhenAny(RouterTask, cancelled, _shutdownRequested.Task).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Stops all daemon components in reverse start order.</summary>
    public async Task StopAsync()
    {
        _presenceMonitor?.Dispose();
        _presenceMonitor = null;

        if (_mcpApp is not null)
        {
            await _mcpApp.StopAsync().ConfigureAwait(false);
            await _mcpApp.DisposeAsync().ConfigureAwait(false);
            _mcpApp = null;
        }

        if (_webApp is not null)
        {
            await _webApp.StopAsync().ConfigureAwait(false);
            await _webApp.DisposeAsync().ConfigureAwait(false);
            _webApp = null;
        }

        _log("[piston] Shutting down.");

        if (_router is not null)
        {
            await _router.DisposeAsync().ConfigureAwait(false);
            _router = null;
        }

        if (_engine is not null)
        {
            _engine.Stop();
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
