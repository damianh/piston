// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.CommandLine;
using Piston;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Hosting;
using Piston.Hosting.Configuration;
using Piston.Hosting.Mapping;
using Piston.Hosting.Protocol;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

// ── Shared arguments & options ─────────────────────────────────────────────────

var solutionArg = new Argument<FileInfo?>(
    name: "solution",
    description: "Path to the .sln, .slnx, or .slnf file. " +
                 "Defaults to .piston.json's solution, then a unique solution in the current directory.",
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
    description: "Use stdin/stdout for Piston JSON-RPC transport (not MCP).");

var noStdioOpt = new Option<bool>(
    name: "--no-stdio",
    description: "Use named-pipe transport, overriding the .piston.json stdio setting.");

var configDirectoryOpt = new Option<string?>("--config-directory")
{
    IsHidden = true,
};

var mcpPortOpt = new Option<int?>(
    name: "--mcp-port",
    description: "Enable MCP server on the specified port.");

var webPortOpt = new Option<int>(
    name: "--web-port",
    description: "Port for the web UI and WebSocket server.",
    getDefaultValue: () => 5199);

// ── Root command (default: web UI with auto-start) ─────────────────────────────

var rootCommand = new RootCommand("Piston — continuous test runner for .NET")
{
    solutionArg,
    pipeNameOpt,
    webPortOpt,
};

rootCommand.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var pipeName = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var webPort = ctx.ParseResult.GetValueForOption(webPortOpt);
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
    noStdioOpt,
    pipeNameOpt,
    mcpPortOpt,
    webPortOpt,
    configDirectoryOpt,
};

daemonCmd.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var debounceMs = ctx.ParseResult.GetValueForOption(debounceOpt);
    var filter = ctx.ParseResult.GetValueForOption(filterOpt);
    var coverage = ctx.ParseResult.GetValueForOption(coverageOpt);
    var parallelism = ctx.ParseResult.GetValueForOption(parallelismOpt);
    var stdio = ctx.ParseResult.GetValueForOption(stdioOpt);
    var noStdio = ctx.ParseResult.GetValueForOption(noStdioOpt);
    var pipeName = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var mcpPort = ctx.ParseResult.GetValueForOption(mcpPortOpt);
    var webPort = ctx.ParseResult.GetValueForOption(webPortOpt);
    var configDirectory = ctx.ParseResult.GetValueForOption(configDirectoryOpt);

    if (stdio && noStdio)
    {
        Console.Error.WriteLine("error: --stdio and --no-stdio cannot be used together.");
        ctx.ExitCode = 1;
        return;
    }

    // Resolve config before choosing the daemon transport.
    if (!stdio && !noStdio)
    {
        try
        {
            var (_, config) = HostHelpers.ResolveSolution(solutionFile, configurationDirectory: configDirectory);
            stdio = config.Stdio ?? false;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            ctx.ExitCode = 1;
            return;
        }
    }
    else if (noStdio)
    {
        stdio = false;
    }

    if (stdio)
    {
        await RunDaemonStdioAsync(solutionFile, debounceMs, filter, coverage, parallelism, configDirectory);
    }
    else
    {
        await RunDaemonAsync(solutionFile, debounceMs, filter, coverage, parallelism, pipeName, mcpPort, webPort,
            configDirectory);
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
    var pipeName = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var exitCode = await RunStopAsync(solutionFile, pipeName);
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
    var pipeName = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var exitCode = await RunStatusAsync(solutionFile, pipeName);
    ctx.ExitCode = exitCode;
});

rootCommand.AddCommand(statusCmd);

return await rootCommand.InvokeAsync(args);

// ── Web mode (default) — auto-starts daemon if needed, opens browser ──────────

static async Task RunWebAsync(FileInfo? solutionArg, string? cliPipeName, int webPort)
{
    string solutionPath;
    PistonConfig config;
    try
    {
        (solutionPath, config) = HostHelpers.ResolveSolution(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var pipeName = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    // Ensure daemon is running (auto-start if not)
    var configurationDirectory = solutionArg is null
        ? Directory.GetCurrentDirectory()
        : Path.GetDirectoryName(solutionPath)!;
    try
    {
        await DaemonLauncher.EnsureRunningAsync(solutionPath, pipeName, webPort, configurationDirectory, cts.Token);
    }
    catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

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
    int webPort,
    string? configDirectory)
{
    string solutionPath;
    PistonConfig config;
    PistonOptions options;
    try
    {
        (solutionPath, config) = HostHelpers.ResolveSolution(solutionArg, configurationDirectory: configDirectory);
        options = HostHelpers.BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    if (!HostHelpers.DotnetSdkAvailable())
    {
        Console.Error.WriteLine("error: 'dotnet' SDK not found on PATH. Install .NET 10 SDK from https://dot.net");
        Environment.Exit(1);
        return;
    }

    var pipeName = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);
    var mcpPort = cliMcpPort ?? config.McpPort;

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await using var host = new DaemonHost(new DaemonHostOptions
    {
        SolutionPath = solutionPath,
        EngineOptions = options,
        PipeName = pipeName,
        WebPort = webPort,
        McpPort = mcpPort,
    });

    try
    {
        await host.StartAsync(cts.Token);
    }
    catch (ExecutionCoordinationException ex)
    {
        // Worktree already owned, or machine-wide execution coordination unusable: never run unowned.
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }
    catch (DaemonStartupException ex)
    {
        // A web or MCP port is in use; components already started were stopped by the host.
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    // Clients (e.g. DaemonLauncher) read this line from stdout to discover the pipe.
    Console.WriteLine($"PIPE:{pipeName}");

    await host.WaitForShutdownAsync(cts.Token);
}

// ── Daemon stdio mode ──────────────────────────────────────────────────────────

static async Task RunDaemonStdioAsync(
    FileInfo? solutionArg,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism,
    string? configDirectory)
{
    string solutionPath;
    PistonConfig config;
    PistonOptions options;
    try
    {
        (solutionPath, config) = HostHelpers.ResolveSolution(solutionArg, configurationDirectory: configDirectory);
        options = HostHelpers.BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    if (!HostHelpers.DotnetSdkAvailable())
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

    PistonEngine engine;
    try
    {
        engine = new PistonEngine(options);
        Console.Error.WriteLine($"[piston] Starting engine for: {solutionPath}");
        await engine.StartAsync(solutionPath);
    }
    catch (ExecutionCoordinationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }
    using var engineScope = engine;
    Console.Error.WriteLine("[piston] Listening on stdio.");

    // NOTE: Do NOT write PIPE: line — stdout is reserved for JSON-RPC in stdio mode.
    var stdinStream = Console.OpenStandardInput();
    var stdoutStream = Console.OpenStandardOutput();
    var duplexStream = new StdioDuplexStream(stdinStream, stdoutStream);

    var dispatcher = new EngineCommandDispatcher(engine);
    var session = new ClientSession(duplexStream, "stdio-session", dispatcher);

    engine.State.StateChanged += OnEngineStateChanged;

    try
    {
        var snapshotNotification = HostHelpers.BuildStateSnapshot(engine);
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

        SendFireAndForget(HostHelpers.ToNotification(ProtocolMethods.EngineStateSnapshot, stateSnapshot));
        SendFireAndForget(HostHelpers.ToNotification(
            ProtocolMethods.EnginePhaseChanged,
            new PhaseChangedNotification(stateSnapshot.Phase, null)));

        if (engine.State.Phase == PistonPhase.Testing)
        {
            SendFireAndForget(HostHelpers.ToNotification(
                ProtocolMethods.TestsProgress,
                new TestProgressNotification(
                    stateSnapshot.InProgressSuites,
                    stateSnapshot.CompletedTests,
                    stateSnapshot.TotalExpectedTests)));
        }

        if (engine.State.Phase == PistonPhase.Error && stateSnapshot.LastBuild is not null)
        {
            SendFireAndForget(HostHelpers.ToNotification(
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
                {
                    Console.Error.WriteLine($"[piston] Failed to send notification: {t.Exception?.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
    }
}

// ── stop command ───────────────────────────────────────────────────────────────

static async Task<int> RunStopAsync(FileInfo? solutionArg, string? cliPipeName)
{
    string solutionPath;
    PistonConfig config;
    try
    {
        (solutionPath, config) = HostHelpers.ResolveSolution(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    var pipeName = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);

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

    DaemonInfoResponse? info;
    try
    {
        info = await client.GetDaemonInfoAsync();
    }
    catch (InvalidOperationException)
    {
        // Daemons without daemon/info cannot exit on request; stopping the engine is all we can do.
        await client.StopAsync(CancellationToken.None);
        Console.Error.WriteLine("[piston] Engine stopped, but this daemon does not support shutdown; its process is still running.");
        return 1;
    }

    try
    {
        await client.ShutdownDaemonAsync();
    }
    catch (IOException)
    {
        // The daemon may close the pipe before the reply arrives; the exit wait below decides.
    }

    await client.DisposeAsync();

    var pid = info!.ProcessId;
    if (!await WaitForProcessExitAsync(pid, TimeSpan.FromSeconds(30)))
    {
        Console.Error.WriteLine($"error: daemon (PID {pid}) did not exit within 30s.");
        return 1;
    }

    Console.Error.WriteLine($"[piston] Daemon stopped (PID {pid}).");
    return 0;
}

static async Task<bool> WaitForProcessExitAsync(int pid, TimeSpan timeout)
{
    try
    {
        using var process = System.Diagnostics.Process.GetProcessById(pid);
        using var cts = new CancellationTokenSource(timeout);
        await process.WaitForExitAsync(cts.Token);
        return true;
    }
    catch (ArgumentException)
    {
        return true; // already exited
    }
    catch (OperationCanceledException)
    {
        return false;
    }
}

static string DescribeRunDto(TestRunDto run) =>
    $"{run.RunId} | {run.Trigger} | {run.Status} | scope {run.Scope ?? "unknown"} | {run.Freshness} | " +
    $"current full result: {(run.IsCurrentFullResult ? "yes" : "no")} | " +
    $"{run.TotalTests} tests ({run.Passed} passed, {run.Failed} failed, {run.Skipped} skipped)";

// ── status command ─────────────────────────────────────────────────────────────

static async Task<int> RunStatusAsync(FileInfo? solutionArg, string? cliPipeName)
{
    string solutionPath;
    PistonConfig config;
    try
    {
        (solutionPath, config) = HostHelpers.ResolveSolution(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }

    var pipeName = cliPipeName ?? config.PipeName ?? NamedPipeListener.GeneratePipeName(solutionPath);

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
    {
        snapshot = client.CurrentSnapshot;
    }
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

    DaemonInfoResponse? info = null;
    try
    {
        info = await client.GetDaemonInfoAsync();
    }
    catch (InvalidOperationException)
    {
        // Older daemon without daemon/info.
    }

    Console.WriteLine($"Daemon running.");
    Console.WriteLine($"  Solution : {info?.SolutionPath ?? solutionPath}");
    if (info is not null)
    {
        Console.WriteLine($"  PID      : {info.ProcessId}");
        Console.WriteLine($"  Started  : {info.StartedAt:u}");
        Console.WriteLine($"  Version  : {info.Version ?? "unknown"}");
        Console.WriteLine($"  Web      : {info.WebUrl}");
        Console.WriteLine($"  MCP      : {info.McpUrl ?? "disabled (set mcpPort in .piston.json or pass --mcp-port)"}");
    }

    Console.WriteLine($"  Pipe     : {pipeName}");
    Console.WriteLine($"  Phase    : {snapshot.Phase}");
    Console.WriteLine($"  Tests    : {snapshot.CompletedTests}/{snapshot.TotalExpectedTests}");
    Console.WriteLine($"  Suites   : {snapshot.Suites.Count}");
    Console.WriteLine($"  Latest   : {(snapshot.LastRun is { } last ? DescribeRunDto(last) : "(none)")}");
    if (snapshot.ActiveRun is { } active)
    {
        Console.WriteLine($"  Active   : {DescribeRunDto(active)}");
    }

    return 0;
}
