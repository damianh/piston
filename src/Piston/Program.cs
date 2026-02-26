using System.CommandLine;
using Microsoft.Extensions.Configuration;
using Piston.Configuration;
using Piston.Core;
using Piston.Core.Orchestration;
using Piston.Core.Services;
using Piston.Views;
using SharpConsoleUI;
using SharpConsoleUI.Drivers;

// ── CLI definition ─────────────────────────────────────────────────────────────

var solutionArg = new Argument<FileInfo?>(
    name: "solution",
    description: "Path to the .sln or .slnx file to watch. " +
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

var rootCommand = new RootCommand("Piston — continuous test runner for .NET")
{
    solutionArg,
    debounceOpt,
    filterOpt,
};

rootCommand.SetHandler(async (FileInfo? solutionFile, int debounceMs, string? filter) =>
{
    await RunAsync(solutionFile, debounceMs, filter);
}, solutionArg, debounceOpt, filterOpt);

return await rootCommand.InvokeAsync(args);

// ── Main entrypoint ────────────────────────────────────────────────────────────

static async Task RunAsync(FileInfo? solutionArg, int cliDebounceMs, string? cliFilter)
{
    // 1. Find solution path
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

    // 2. Load .piston.json from solution directory (optional)
    var solutionDir = Path.GetDirectoryName(solutionPath)!;
    var config = LoadConfig(solutionDir);

    // 3. Merge options: CLI > config > defaults
    var debounceMs = cliDebounceMs > 0 ? cliDebounceMs
        : config.DebounceMs is > 0 ? config.DebounceMs.Value
        : 300;

    var filter = cliFilter ?? config.TestFilter;

    var options = new PistonOptions
    {
        SolutionPath    = solutionPath,
        DebounceInterval = TimeSpan.FromMilliseconds(debounceMs),
        TestFilter      = filter,
    };

    // 4. Validate .NET SDK is available
    if (!DotnetSdkAvailable())
    {
        Console.Error.WriteLine("error: 'dotnet' SDK not found on PATH. Install .NET 10 SDK from https://dot.net");
        Environment.Exit(1);
        return;
    }

    // 5. Compose services
    var state = new PistonState
    {
        TestFilter = options.TestFilter,
    };

    var fileWatcher = new FileWatcherService(options.DebounceInterval);
    var buildService = new BuildService();
    var trxParser = new TrxResultParser();
    var testRunner = new TestRunnerService(trxParser);
    var orchestrator = new PistonOrchestrator(fileWatcher, buildService, testRunner, state);

    // 6. Start TUI
    var driver = new NetConsoleDriver(RenderMode.Buffer);
    var windowSystem = new ConsoleWindowSystem(driver);

    // Route Ctrl+C through the TUI's own shutdown path so the driver can
    // run its full terminal-cleanup sequence (disabling mouse tracking etc.)
    // rather than relying on the emergency AppDomain.ProcessExit handler.
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;          // prevent abrupt process kill
        windowSystem.Shutdown();  // triggers normal driver.Stop() cleanup
    };

    // Switch to the alternate screen buffer so the TUI doesn't mix with the
    // shell's scroll-back history.  Restore it on both normal and abrupt exit.
    Console.Write("\x1b[?1049h");   // enter alternate screen
    AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Write("\x1b[?1049l");

    PistonWindow.Create(windowSystem, state, orchestrator);

    // 7. Start watching in background (after TUI is set up)
    await orchestrator.StartAsync(options.SolutionPath);

    // 8. Run TUI (blocks until Q / Ctrl+C)
    windowSystem.Run();

    // 9. Graceful shutdown
    orchestrator.Stop();
    orchestrator.Dispose();

    Console.Write("\x1b[?1049l");   // leave alternate screen
}

// ── Helpers ────────────────────────────────────────────────────────────────────

static string ResolveSolutionPath(FileInfo? solutionArg)
{
    if (solutionArg is not null)
    {
        if (!solutionArg.Exists)
            throw new InvalidOperationException($"Solution file not found: {solutionArg.FullName}");

        var ext = solutionArg.Extension.ToLowerInvariant();
        if (ext is not ".sln" and not ".slnx")
            throw new InvalidOperationException($"Expected a .sln or .slnx file, got: {solutionArg.Name}");

        return solutionArg.FullName;
    }

    // Auto-discover in current directory
    var cwd = Directory.GetCurrentDirectory();
    var candidates = Directory.GetFiles(cwd, "*.sln")
        .Concat(Directory.GetFiles(cwd, "*.slnx"))
        .ToList();

    return candidates.Count switch
    {
        0 => throw new InvalidOperationException(
            $"No .sln or .slnx file found in '{cwd}'. Pass the solution path explicitly."),
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
        // Malformed config — ignore and use defaults
        return new PistonConfig();
    }
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
