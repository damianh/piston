using System.CommandLine;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Piston.Cli;
using Piston.Cli.Configuration;
using Piston.Cli.Mapping;
using Piston.Cli.Protocol;
using Piston.Cli.Services;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Mcp;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;
using Piston.Roslyn;

// ── Shared arguments & options ─────────────────────────────────────────────────

var solutionArg = new Argument<FileInfo?>(
    name: "solution",
    description: "Path to the .sln, .slnx, or .slnf file. " +
                 "Defaults to the first solution found in the current directory.",
    getDefaultValue: () => null);

var pipeNameOpt = new Option<string?>(
    name: "--pipe-name",
    description: "Override the named pipe name (default: auto-generated from solution path).");

// ── Daemon subcommand options ──────────────────────────────────────────────────

var debounceOpt = new Option<int>(
    name: "--debounce",
    description: "File-change debounce interval in milliseconds.",
    getDefaultValue: () => 0);

var filterOpt = new Option<string?>(
    name: "--filter",
    description: "Substring or regex to filter test names on startup.",
    getDefaultValue: () => null);

var coverageOpt = new Option<bool>(
    name: "--coverage",
    description: "Enable code coverage collection during test runs.",
    getDefaultValue: () => false);

var parallelismOpt = new Option<int>(
    name: "--parallelism",
    description: "Maximum number of concurrent dotnet test processes. 0 = auto.",
    getDefaultValue: () => 0);

var stdioOpt = new Option<bool>(
    name: "--stdio",
    description: "Use stdin/stdout for JSON-RPC transport (for IDE extensions).");

var mcpPortOpt = new Option<int?>(
    name: "--mcp-port",
    description: "Enable MCP server on the specified port.");

var webPortOpt = new Option<int>(
    name: "--web-port",
    description: "Port for the web UI and WebSocket server.",
    getDefaultValue: () => 5199);

// ── Root command (default: TUI with auto-start) ────────────────────────────────

var rootCommand = new RootCommand("Piston — continuous test runner for .NET")
{
    solutionArg,
    pipeNameOpt,
    webPortOpt,
};

rootCommand.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var pipeName     = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var webPort      = ctx.ParseResult.GetValueForOption(webPortOpt);
    await RunWebAsync(solutionFile, pipeName, webPort);
});

// ── daemon subcommand ──────────────────────────────────────────────────────────

var daemonCmd = new Command("daemon", "Start the Piston daemon in the foreground.")
{
    solutionArg,
    debounceOpt,
    filterOpt,
    coverageOpt,
    parallelismOpt,
    stdioOpt,
    pipeNameOpt,
    mcpPortOpt,
    webPortOpt,
};

daemonCmd.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var debounceMs   = ctx.ParseResult.GetValueForOption(debounceOpt);
    var filter       = ctx.ParseResult.GetValueForOption(filterOpt);
    var coverage     = ctx.ParseResult.GetValueForOption(coverageOpt);
    var parallelism  = ctx.ParseResult.GetValueForOption(parallelismOpt);
    var stdio        = ctx.ParseResult.GetValueForOption(stdioOpt);
    var pipeName     = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var mcpPort      = ctx.ParseResult.GetValueForOption(mcpPortOpt);
    var webPort      = ctx.ParseResult.GetValueForOption(webPortOpt);

    // Check config for stdio/mcpPort defaults (best-effort; errors handled inside the run methods)
    if (!stdio)
    {
        try
        {
            var solutionPath = CliHelpers.ResolveSolutionPath(solutionFile);
            var config = CliHelpers.LoadConfig(Path.GetDirectoryName(solutionPath)!);
            stdio = config.Stdio ?? false;
        }
        catch { /* fall through; errors reported inside run methods */ }
    }

    if (stdio)
    {
        await RunDaemonStdioAsync(solutionFile, debounceMs, filter, coverage, parallelism);
    }
    else
    {
        await RunDaemonAsync(solutionFile, debounceMs, filter, coverage, parallelism, pipeName, mcpPort, webPort);
    }
});

rootCommand.AddCommand(daemonCmd);

// ── stop subcommand ────────────────────────────────────────────────────────────

var stopCmd = new Command("stop", "Stop the running Piston daemon.")
{
    solutionArg,
    pipeNameOpt,
};

stopCmd.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var pipeName     = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var exitCode     = await RunStopAsync(solutionFile, pipeName);
    ctx.ExitCode = exitCode;
});

rootCommand.AddCommand(stopCmd);

// ── status subcommand ──────────────────────────────────────────────────────────

var statusCmd = new Command("status", "Show the status of the running Piston daemon.")
{
    solutionArg,
    pipeNameOpt,
};

statusCmd.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var pipeName     = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var exitCode     = await RunStatusAsync(solutionFile, pipeName);
    ctx.ExitCode = exitCode;
});

rootCommand.AddCommand(statusCmd);

return await rootCommand.InvokeAsync(args);

// ── Web mode (default) — auto-starts daemon if needed, opens browser ──────────

static async Task RunWebAsync(FileInfo? solutionArg, string? cliPipeName, int webPort)
{
    string solutionPath;
    try
    {
        solutionPath = CliHelpers.ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = CliHelpers.LoadConfig(solutionDir);
    var pipeName    = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    // Ensure daemon is running (auto-start if not)
    await DaemonLauncher.EnsureRunningAsync(solutionPath, pipeName, cts.Token);

    var webUrl = $"http://localhost:{webPort}";
    Console.Error.WriteLine($"[piston] Opening browser: {webUrl}");

    // Brief delay to let the daemon's web server start up
    await Task.Delay(500, cts.Token).ConfigureAwait(false);

    BrowserLauncher.Open(webUrl);

    // Keep the process alive until Ctrl+C
    Console.Error.WriteLine("[piston] Press Ctrl+C to exit.");
    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { }
}

// ── Daemon mode (foreground) ───────────────────────────────────────────────────

static async Task RunDaemonAsync(
    FileInfo? solutionArg,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism,
    string? cliPipeName,
    int? cliMcpPort,
    int webPort)
{
    string solutionPath;
    try
    {
        solutionPath = CliHelpers.ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = CliHelpers.LoadConfig(solutionDir);
    var options     = CliHelpers.BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);

    if (!CliHelpers.DotnetSdkAvailable())
    {
        Console.Error.WriteLine("error: 'dotnet' SDK not found on PATH. Install .NET 10 SDK from https://dot.net");
        Environment.Exit(1);
        return;
    }

    var pipeName = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);
    var mcpPort  = cliMcpPort ?? config.McpPort;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    // Use a proxy sink so the router (created after engine) can receive activity events.
    var activityProxy = new ActivityEventSinkProxy();
    using var engine = new PistonEngine(options, activityProxy);

    Console.Error.WriteLine($"[piston] Starting engine for: {solutionPath}");
    await engine.StartAsync(solutionPath);

    // Start Roslyn workspace in the background (child process, OOM-isolated)
    await using var workspace = RoslynWorkspaceFactory.Create();
    _ = Task.Run(async () =>
    {
        try
        {
            var info = await workspace.LoadAsync(solutionPath, cts.Token).ConfigureAwait(false);
            Console.Error.WriteLine($"[piston] Roslyn workspace loaded: {info.Projects.Count} project(s)");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[piston] Roslyn workspace failed to load: {ex.Message}");
        }
    });

    Console.Error.WriteLine($"[piston] Listening on pipe: {pipeName}");
    Console.WriteLine($"PIPE:{pipeName}");

    var listener = new NamedPipeListener(pipeName);
    await using var router = new ProtocolRouter(engine, listener);

    // Connect the activity proxy to the router so engine events are broadcast to clients
    activityProxy.SetSink(router);

    // Wire diagnostic watcher and MCP call tracker into the router
    using var diagnosticWatcher = new DiagnosticWatcherService(workspace, router, solutionPath);
    var mcpCallTracker = new McpCallTracker(router, solutionPath);
    router.SetDiagnosticWatcher(diagnosticWatcher);
    router.SetMcpCallTracker(mcpCallTracker);
    diagnosticWatcher.Start();

    var routerTask = router.RunAsync(cts.Token);

    // Always start the web server for WebSocket + static file serving
    var webBuilder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ApplicationName = "Piston",
        // Set content root to the directory containing the host binary so that
        // UseStaticFiles() can find the wwwroot/ folder with Blazor WASM assets.
        ContentRootPath = AppContext.BaseDirectory,
    });

    var webApp = webBuilder.Build();
    webApp.Urls.Add($"http://localhost:{webPort}");
    webApp.UseWebSockets();

    // Serve Blazor WASM framework files (handles content negotiation for .br/.gz compressed assets)
    // and static files. UseBlazorFrameworkFiles must come before UseStaticFiles.
    webApp.UseBlazorFrameworkFiles();
    webApp.UseDefaultFiles();
    webApp.UseStaticFiles();
    webApp.UseRouting();

    webApp.Map("/ws", async context =>
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        await router.AddWebSocketSessionAsync(webSocket, cts.Token).ConfigureAwait(false);
    });

    // Fallback to index.html for Blazor SPA client-side routing
    webApp.MapFallbackToFile("index.html");

    Console.Error.WriteLine($"[piston] Web server (WebSocket) listening on port: {webPort}");

    if (mcpPort is not null)
    {
        // MCP always runs on its own dedicated app to avoid middleware conflicts
        var mcpBuilder = WebApplication.CreateBuilder();
        mcpBuilder.Services.AddSingleton<IEngine>(engine);
        mcpBuilder.Services.AddPistonMcp(workspace, mcpCallTracker);
        var mcpApp = mcpBuilder.Build();
        mcpApp.MapMcp();

        Console.Error.WriteLine($"[piston] MCP server listening on port: {mcpPort}");
        var mcpTask = mcpApp.RunAsync($"http://localhost:{mcpPort}");

        try
        {
            await Task.WhenAny(routerTask, webApp.RunAsync(), mcpTask);
        }
        catch (OperationCanceledException) { }

        await mcpApp.StopAsync();
        await mcpApp.DisposeAsync();
    }
    else
    {
        try
        {
            await Task.WhenAny(routerTask, webApp.RunAsync());
        }
        catch (OperationCanceledException) { }
    }

    await webApp.StopAsync();
    await webApp.DisposeAsync();

    Console.Error.WriteLine("[piston] Shutting down.");
    engine.Stop();
}

// ── Daemon stdio mode ──────────────────────────────────────────────────────────

static async Task RunDaemonStdioAsync(
    FileInfo? solutionArg,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism)
{
    string solutionPath;
    try
    {
        solutionPath = CliHelpers.ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = CliHelpers.LoadConfig(solutionDir);
    var options     = CliHelpers.BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);

    if (!CliHelpers.DotnetSdkAvailable())
    {
        Console.Error.WriteLine("error: 'dotnet' SDK not found on PATH. Install .NET 10 SDK from https://dot.net");
        Environment.Exit(1);
        return;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var engine = new PistonEngine(options);

    Console.Error.WriteLine($"[piston] Starting engine for: {solutionPath}");
    await engine.StartAsync(solutionPath);
    Console.Error.WriteLine("[piston] Listening on stdio.");

    // NOTE: Do NOT write PIPE: line — stdout is reserved for JSON-RPC in stdio mode.
    var stdinStream  = Console.OpenStandardInput();
    var stdoutStream = Console.OpenStandardOutput();
    var duplexStream = new StdioDuplexStream(stdinStream, stdoutStream);

    var dispatcher = new EngineCommandDispatcher(engine);
    var session    = new ClientSession(duplexStream, "stdio-session", dispatcher);

    engine.State.StateChanged += OnEngineStateChanged;

    try
    {
        var snapshotNotification = CliHelpers.BuildStateSnapshot(engine);
        await session.SendNotificationAsync(snapshotNotification, cts.Token).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[piston] Failed to send initial snapshot: {ex.Message}");
        engine.State.StateChanged -= OnEngineStateChanged;
        engine.Stop();
        return;
    }

    try
    {
        await session.RunAsync(cts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) { }
    finally
    {
        engine.State.StateChanged -= OnEngineStateChanged;
    }

    Console.Error.WriteLine("[piston] Shutting down.");
    engine.Stop();

    void OnEngineStateChanged()
    {
        var stateSnapshot = engine.State.ToSnapshot();

        SendFireAndForget(CliHelpers.ToNotification(ProtocolMethods.EngineStateSnapshot, stateSnapshot));
        SendFireAndForget(CliHelpers.ToNotification(
            ProtocolMethods.EnginePhaseChanged,
            new PhaseChangedNotification(stateSnapshot.Phase, null)));

        if (engine.State.Phase == PistonPhase.Testing)
        {
            SendFireAndForget(CliHelpers.ToNotification(
                ProtocolMethods.TestsProgress,
                new TestProgressNotification(
                    stateSnapshot.InProgressSuites,
                    stateSnapshot.CompletedTests,
                    stateSnapshot.TotalExpectedTests)));
        }

        if (engine.State.Phase == PistonPhase.Error && stateSnapshot.LastBuild is not null)
        {
            SendFireAndForget(CliHelpers.ToNotification(
                ProtocolMethods.BuildError,
                new BuildErrorNotification(stateSnapshot.LastBuild)));
        }
    }

    void SendFireAndForget(JsonRpcNotification notification)
    {
        _ = session.SendNotificationAsync(notification, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Console.Error.WriteLine($"[piston] Failed to send notification: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
    }
}

// ── stop command ───────────────────────────────────────────────────────────────

static async Task<int> RunStopAsync(FileInfo? solutionArg, string? cliPipeName)
{
    string solutionPath;
    try
    {
        solutionPath = CliHelpers.ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = CliHelpers.LoadConfig(solutionDir);
    var pipeName    = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);

    await using var client = new RemoteEngineClient(pipeName);

    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        await client.ConnectAsync(connectCts.Token);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("No daemon running (could not connect within 5s).");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"No daemon running: {ex.Message}");
        return 0;
    }

    await client.StopAsync(CancellationToken.None);
    Console.Error.WriteLine("[piston] Daemon stopped.");
    return 0;
}

// ── status command ─────────────────────────────────────────────────────────────

static async Task<int> RunStatusAsync(FileInfo? solutionArg, string? cliPipeName)
{
    string solutionPath;
    try
    {
        solutionPath = CliHelpers.ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = CliHelpers.LoadConfig(solutionDir);
    var pipeName    = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);

    await using var client = new RemoteEngineClient(pipeName);

    using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    try
    {
        await client.ConnectAsync(connectCts.Token);
    }
    catch
    {
        Console.WriteLine("No daemon running for this solution.");
        Console.WriteLine($"  Solution : {solutionPath}");
        Console.WriteLine($"  Pipe     : {pipeName}");
        return 1;
    }

    // Wait briefly for the initial state snapshot that ProtocolRouter sends on connect
    using var snapshotCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    StateSnapshotNotification? snapshot = null;
    client.StateChanged += _ => snapshot = client.CurrentSnapshot;

    if (client.CurrentSnapshot is not null)
        snapshot = client.CurrentSnapshot;
    else
    {
        try { await Task.Delay(500, snapshotCts.Token); } catch (OperationCanceledException) { }
        snapshot = client.CurrentSnapshot;
    }

    if (snapshot is null)
    {
        Console.WriteLine("Daemon is running but no state available yet.");
        Console.WriteLine($"  Pipe: {pipeName}");
        return 0;
    }

    Console.WriteLine($"Daemon running.");
    Console.WriteLine($"  Solution : {solutionPath}");
    Console.WriteLine($"  Pipe     : {pipeName}");
    Console.WriteLine($"  Phase    : {snapshot.Phase}");
    Console.WriteLine($"  Tests    : {snapshot.CompletedTests}/{snapshot.TotalExpectedTests}");
    Console.WriteLine($"  Suites   : {snapshot.Suites.Count}");
    return 0;
}
