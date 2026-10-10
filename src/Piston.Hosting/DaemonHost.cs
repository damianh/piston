using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piston.Engine;
using Piston.Hosting.Protocol;
using Piston.Hosting.Services;
using Piston.Mcp;
using Piston.Protocol.Transports;

namespace Piston.Hosting;

/// <summary>
/// Hosts the full Piston daemon composition in-process: engine,
/// protocol router (named pipe + WebSocket), web dashboard server, and optional MCP server.
/// Shared by the CLI (<c>piston daemon</c>) and the desktop shell.
/// </summary>
public sealed class DaemonHost : IAsyncDisposable
{
    private readonly DaemonHostOptions _options;
    private readonly Action<string> _log;

    private PistonEngine? _engine;
    private ProtocolRouter? _router;
    private WebApplication? _webApp;
    private WebApplication? _mcpApp;
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
    public async Task StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Use a proxy sink so the router (created after engine) can receive activity events.
        var activityProxy = new ActivityEventSinkProxy();
        _engine = new PistonEngine(_options.EngineOptions, activityProxy);

        _log($"[piston] Starting engine for: {_options.SolutionPath}");
        await _engine.StartAsync(_options.SolutionPath).ConfigureAwait(false);

        _log($"[piston] Listening on pipe: {_options.PipeName}");

        var listener = new NamedPipeListener(_options.PipeName);
        _router = new ProtocolRouter(_engine, listener);

        // Connect the activity proxy to the router so engine events are broadcast to clients
        activityProxy.SetSink(_router);

        // Wire MCP call tracker into the router
        var mcpCallTracker = new McpCallTracker(_router, _options.SolutionPath);
        _router.SetMcpCallTracker(mcpCallTracker);

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
        _webApp.UseWebSockets();

        // Serve Blazor WASM framework files (handles content negotiation for .br/.gz compressed
        // assets) and static files. UseBlazorFrameworkFiles must come before UseStaticFiles.
        _webApp.UseBlazorFrameworkFiles();
        _webApp.UseDefaultFiles();
        _webApp.UseStaticFiles();
        _webApp.UseRouting();

        var router = _router;
        _webApp.Map("/ws", async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
            await router.AddWebSocketSessionAsync(webSocket, ct).ConfigureAwait(false);
        });

        // Fallback to index.html for Blazor SPA client-side routing
        _webApp.MapFallbackToFile("index.html");

        await _webApp.StartAsync(ct).ConfigureAwait(false);
        _log($"[piston] Web server (WebSocket) listening on port: {_options.WebPort}");

        if (_options.McpPort is not null)
        {
            // MCP always runs on its own dedicated app to avoid middleware conflicts
            var mcpBuilder = WebApplication.CreateBuilder();
            mcpBuilder.Services.AddSingleton<IEngine>(_engine);
            mcpBuilder.Services.AddPistonMcp(mcpCallTracker);
            _mcpApp = mcpBuilder.Build();
            _mcpApp.MapMcp();
            _mcpApp.Urls.Add($"http://localhost:{_options.McpPort}");

            await _mcpApp.StartAsync(ct).ConfigureAwait(false);
            _log($"[piston] MCP server listening on port: {_options.McpPort}");
        }
    }

    /// <summary>Task representing the protocol router loop. Available after <see cref="StartAsync"/>.</summary>
    public Task RouterTask { get; private set; } = Task.CompletedTask;

    /// <summary>Waits until cancellation is requested or the router loop ends.</summary>
    public async Task WaitForShutdownAsync(CancellationToken ct)
    {
        try
        {
            var cancelled = Task.Delay(Timeout.Infinite, ct);
            await Task.WhenAny(RouterTask, cancelled).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Stops all daemon components in reverse start order.</summary>
    public async Task StopAsync()
    {
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
            _engine.Dispose();
            _engine = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
