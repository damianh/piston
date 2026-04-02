using System.CommandLine;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Piston.Controller.Configuration;
using Piston.Controller.Mapping;
using Piston.Controller.Protocol;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;
using Piston.Protocol.Transports;

// ── CLI definition ─────────────────────────────────────────────────────────────

var solutionArg = new Argument<FileInfo?>(
    name: "solution",
    description: "Path to the .sln, .slnx, or .slnf file to watch. " +
                 "Defaults to the first solution found in the current directory.",
    getDefaultValue: () => null);

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
    description: "Maximum number of concurrent dotnet test processes. 0 = auto (ProcessorCount / 2).",
    getDefaultValue: () => 0);

var headlessOpt = new Option<bool>(
    name: "--headless",
    description: "Run in headless mode (no TUI). Listens on a named pipe.");

var stdioOpt = new Option<bool>(
    name: "--stdio",
    description: "Use stdin/stdout for JSON-RPC transport (for IDE extensions). Requires --headless.");

var pipeNameOpt = new Option<string?>(
    name: "--pipe-name",
    description: "Override the named pipe name (default: auto-generated from solution path).");

var connectOpt = new Option<string?>(
    name: "--connect",
    description: "Connect to a running headless controller via named pipe.");

var rootCommand = new RootCommand("Piston — continuous test runner for .NET")
{
    solutionArg,
    debounceOpt,
    filterOpt,
    coverageOpt,
    parallelismOpt,
    headlessOpt,
    stdioOpt,
    pipeNameOpt,
    connectOpt,
};

rootCommand.SetHandler(async ctx =>
{
    var solutionFile = ctx.ParseResult.GetValueForArgument(solutionArg);
    var debounceMs   = ctx.ParseResult.GetValueForOption(debounceOpt);
    var filter       = ctx.ParseResult.GetValueForOption(filterOpt);
    var coverage     = ctx.ParseResult.GetValueForOption(coverageOpt);
    var parallelism  = ctx.ParseResult.GetValueForOption(parallelismOpt);
    var headless     = ctx.ParseResult.GetValueForOption(headlessOpt);
    var stdio        = ctx.ParseResult.GetValueForOption(stdioOpt);
    var pipeName     = ctx.ParseResult.GetValueForOption(pipeNameOpt);
    var connect      = ctx.ParseResult.GetValueForOption(connectOpt);

    // Validate exclusivity
    if (headless && connect is not null)
    {
        Console.Error.WriteLine("error: Cannot use --headless and --connect together.");
        ctx.ExitCode = 1;
        return;
    }

    if (stdio && !headless)
    {
        Console.Error.WriteLine("error: --stdio requires --headless.");
        ctx.ExitCode = 1;
        return;
    }

    if (stdio && connect is not null)
    {
        Console.Error.WriteLine("error: Cannot use --stdio and --connect together.");
        ctx.ExitCode = 1;
        return;
    }

    if (connect is not null && (debounceMs > 0 || filter is not null || coverage || parallelism > 0))
    {
        Console.Error.WriteLine("warning: Engine options (--debounce, --filter, --coverage, --parallelism) are ignored in --connect mode.");
    }

    if (connect is not null)
    {
        await RunConnectAsync(connect);
        return;
    }

    if (headless && stdio)
    {
        await RunHeadlessStdioAsync(solutionFile, debounceMs, filter, coverage, parallelism);
        return;
    }

    if (headless)
    {
        await RunHeadlessAsync(solutionFile, debounceMs, filter, coverage, parallelism, pipeName);
        return;
    }

    await RunEmbeddedAsync(solutionFile, debounceMs, filter, coverage, parallelism);
});

return await rootCommand.InvokeAsync(args);

// ── Embedded mode (default) ────────────────────────────────────────────────────

static async Task RunEmbeddedAsync(
    FileInfo? solutionArg,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism)
{
    string solutionPath;
    try
    {
        solutionPath = ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = LoadConfig(solutionDir);
    var options     = BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);

    if (!DotnetSdkAvailable())
    {
        Console.Error.WriteLine("error: 'dotnet' SDK not found on PATH. Install .NET 10 SDK from https://dot.net");
        Environment.Exit(1);
        return;
    }

    await using var client = new Piston.Controller.EmbeddedEngineClient(options);
    await client.StartAsync(solutionPath);
    Piston.Tui.PistonTui.Run(client);
    client.Stop();
}

// ── Headless mode ──────────────────────────────────────────────────────────────

static async Task RunHeadlessAsync(
    FileInfo? solutionArg,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism,
    string? cliPipeName)
{
    string solutionPath;
    try
    {
        solutionPath = ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = LoadConfig(solutionDir);
    var options     = BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);

    if (!DotnetSdkAvailable())
    {
        Console.Error.WriteLine("error: 'dotnet' SDK not found on PATH. Install .NET 10 SDK from https://dot.net");
        Environment.Exit(1);
        return;
    }

    var pipeName = cliPipeName
        ?? config.PipeName
        ?? NamedPipeListener.GeneratePipeName(solutionPath);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var engine = new PistonEngine(options);

    Console.Error.WriteLine($"[piston] Starting engine for: {solutionPath}");
    await engine.StartAsync(solutionPath);

    Console.Error.WriteLine($"[piston] Listening on pipe: {pipeName}");
    Console.WriteLine($"PIPE:{pipeName}");

    var listener = new NamedPipeListener(pipeName);
    await using var router = new ProtocolRouter(engine, listener);

    try
    {
        await router.RunAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        // Normal shutdown
    }

    Console.Error.WriteLine("[piston] Shutting down.");
    engine.Stop();
}

// ── Headless-stdio mode ────────────────────────────────────────────────────────

static async Task RunHeadlessStdioAsync(
    FileInfo? solutionArg,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism)
{
    string solutionPath;
    try
    {
        solutionPath = ResolveSolutionPath(solutionArg);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        Environment.Exit(1);
        return;
    }

    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config      = LoadConfig(solutionDir);
    var options     = BuildOptions(solutionPath, cliDebounceMs, cliFilter, cliCoverage, cliParallelism, config);

    if (!DotnetSdkAvailable())
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

    // NOTE: Do NOT write PIPE: line — stdout is reserved for the JSON-RPC protocol in stdio mode.

    var stdinStream  = Console.OpenStandardInput();
    var stdoutStream = Console.OpenStandardOutput();
    var duplexStream = new StdioDuplexStream(stdinStream, stdoutStream);

    var dispatcher = new EngineCommandDispatcher(engine);
    var session    = new ClientSession(duplexStream, "stdio-session", dispatcher);

    // Subscribe to engine state changes and forward as notifications to the single session.
    engine.State.StateChanged += OnEngineStateChanged;

    // Send initial state snapshot before starting the read loop.
    try
    {
        var snapshotNotification = BuildStateSnapshot(engine);
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
    catch (OperationCanceledException)
    {
        // Normal shutdown
    }
    finally
    {
        engine.State.StateChanged -= OnEngineStateChanged;
    }

    Console.Error.WriteLine("[piston] Shutting down.");
    engine.Stop();

    void OnEngineStateChanged()
    {
        var stateSnapshot = engine.State.ToSnapshot();

        SendNotificationFireAndForget(ToNotification(ProtocolMethods.EngineStateSnapshot, stateSnapshot));

        SendNotificationFireAndForget(ToNotification(
            ProtocolMethods.EnginePhaseChanged,
            new PhaseChangedNotification(stateSnapshot.Phase, null)));

        if (engine.State.Phase == PistonPhase.Testing)
        {
            SendNotificationFireAndForget(ToNotification(
                ProtocolMethods.TestsProgress,
                new TestProgressNotification(
                    stateSnapshot.InProgressSuites,
                    stateSnapshot.CompletedTests,
                    stateSnapshot.TotalExpectedTests)));
        }

        if (engine.State.Phase == PistonPhase.Error && stateSnapshot.LastBuild is not null)
        {
            SendNotificationFireAndForget(ToNotification(
                ProtocolMethods.BuildError,
                new BuildErrorNotification(stateSnapshot.LastBuild)));
        }
    }

    void SendNotificationFireAndForget(JsonRpcNotification notification)
    {
        _ = session.SendNotificationAsync(notification, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsFaulted)
                    Console.Error.WriteLine($"[piston] Failed to send notification: {t.Exception?.GetBaseException().Message}");
            }, TaskScheduler.Default);
    }
}

// ── Connect mode ───────────────────────────────────────────────────────────────
static async Task RunConnectAsync(string pipeName)
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    await using var client = new Piston.Controller.RemoteEngineClient(pipeName);

    try
    {
        await client.ConnectAsync(cts.Token);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"error: Could not connect to headless controller at pipe '{pipeName}'. Is it running?\n  {ex.Message}");
        Environment.Exit(1);
        return;
    }

    Piston.Tui.PistonTui.Run(client);
}

// ── Helpers ────────────────────────────────────────────────────────────────────

static string ResolveSolutionPath(FileInfo? solutionArg)
{
    if (solutionArg is not null)
    {
        if (!solutionArg.Exists)
            throw new InvalidOperationException($"Solution file not found: {solutionArg.FullName}");

        var ext = solutionArg.Extension.ToLowerInvariant();
        if (ext is not ".sln" and not ".slnx" and not ".slnf")
            throw new InvalidOperationException($"Expected a .sln, .slnx, or .slnf file, got: {solutionArg.Name}");

        return solutionArg.FullName;
    }

    var cwd        = Directory.GetCurrentDirectory();
    var candidates = Directory.GetFiles(cwd, "*.sln")
        .Concat(Directory.GetFiles(cwd, "*.slnx"))
        .Concat(Directory.GetFiles(cwd, "*.slnf"))
        .ToList();

    return candidates.Count switch
    {
        0 => throw new InvalidOperationException(
            $"No .sln, .slnx, or .slnf file found in '{cwd}'. Pass the solution path explicitly."),
        1 => candidates[0],
        _ => throw new InvalidOperationException(
            $"Multiple solution files found in '{cwd}'. Pass the solution path explicitly:\n  " +
            string.Join("\n  ", candidates.Select(Path.GetFileName))),
    };
}

static PistonConfig LoadConfig(string solutionDir)
{
    var configPath = Path.Combine(solutionDir, ".piston.json");
    if (!File.Exists(configPath))
        return new PistonConfig();

    try
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(configPath, optional: true, reloadOnChange: false)
            .Build();

        var config = new PistonConfig();
        configuration.Bind(config);
        return config;
    }
    catch
    {
        return new PistonConfig();
    }
}

static PistonOptions BuildOptions(
    string solutionPath,
    int cliDebounceMs,
    string? cliFilter,
    bool cliCoverage,
    int cliParallelism,
    PistonConfig config)
{
    var debounceMs = cliDebounceMs > 0 ? cliDebounceMs
        : config.DebounceMs is > 0 ? config.DebounceMs.Value
        : 300;

    var filter = cliFilter ?? config.TestFilter;

    var coverageEnabled = cliCoverage || (config.CoverageEnabled ?? false);

    var processPoolSize = cliParallelism > 0 ? cliParallelism
        : config.Parallelism is > 0 ? config.Parallelism.Value
        : 0;

    var processRecycleAfter = config.ProcessRecycleAfter is > 0 ? config.ProcessRecycleAfter.Value : 50;

    return new PistonOptions
    {
        SolutionPath        = solutionPath,
        DebounceInterval    = TimeSpan.FromMilliseconds(debounceMs),
        TestFilter          = filter,
        CoverageEnabled     = coverageEnabled,
        ProcessPoolSize     = processPoolSize,
        ProcessRecycleAfter = processRecycleAfter,
    };
}

static bool DotnetSdkAvailable()
{
    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(3_000);
        return p?.ExitCode == 0;
    }
    catch
    {
        return false;
    }
}

static JsonRpcNotification BuildStateSnapshot(IEngine engine)
{
    var snapshot = engine.State.ToSnapshot();
    return ToNotification(ProtocolMethods.EngineStateSnapshot, snapshot);
}

static JsonRpcNotification ToNotification<T>(string method, T payload)
{
    var paramsNode = JsonNode.Parse(
        System.Text.Json.JsonSerializer.Serialize(payload, JsonRpcSerializer.Options));
    return new JsonRpcNotification(method, paramsNode);
}
