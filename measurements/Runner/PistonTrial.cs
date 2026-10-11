using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Piston.Measurements;

public sealed record PistonExecution(string RunId, string RunJson, double ElapsedMs,
    int DaemonPid, double CpuSecondsObservedLowerBound, long PeakRssBytesSampled);

/// <summary>
/// Daemon launch policy. <see cref="ExecutionDirectory"/> isolates machine-wide admission from the
/// operator's own daemons and from other trials; it starts empty, so the engine's default budget applies.
/// </summary>
public sealed record PistonLaunch(string DaemonDll, string Workspace, string Solution, string Artifacts,
    int Parallelism, string ExecutionDirectory)
{
    public const string ExecutionDirectoryVariable = "PISTON_EXECUTION_DIRECTORY";
}

public interface IPistonTrial : IAsyncDisposable
{
    Task Prepare(string fingerprint, IReadOnlyDictionary<string, string> expected, CancellationToken cancellationToken);
    Task<PistonExecution> Run(string fingerprint, CancellationToken cancellationToken);
}

/// <summary>Owns one isolated real daemon; never substitutes latest-state results for a requested run.</summary>
public sealed class PistonTrial : IPistonTrial
{
    private readonly Process daemon;
    private readonly PistonMcpClient mcp;
    private readonly string artifacts;
    private readonly string solution;
    private readonly StreamWriter log;
    private readonly Task logs;
    private string? startupId;

    public PistonTrial(PistonLaunch launch)
    {
        var (dll, workspace, solution, artifacts) = (launch.DaemonDll, launch.Workspace, launch.Solution, launch.Artifacts);
        if (launch.Parallelism < 1) throw new ArgumentException("Piston parallelism must be positive");
        if (!Path.IsPathFullyQualified(launch.ExecutionDirectory) || Directory.Exists(launch.ExecutionDirectory))
            throw new ArgumentException("Piston execution directory must be a new absolute path");
        this.artifacts = artifacts;
        this.solution = solution;
        var mcpPort = FreePort();
        var webPort = FreePort();
        while (webPort == mcpPort) webPort = FreePort();
        string[] command = ["dotnet", dll, "daemon", solution, "--no-stdio",
            "--mcp-port", mcpPort.ToString(CultureInfo.InvariantCulture),
            "--web-port", webPort.ToString(CultureInfo.InvariantCulture),
            "--pipe-name", "piston-measurement-" + Guid.NewGuid().ToString("N"),
            "--config-directory", Path.Combine(artifacts, "piston-config"), "--parallelism", launch.Parallelism.ToString(CultureInfo.InvariantCulture)];
        var info = new ProcessStartInfo(command[0])
        {
            WorkingDirectory = workspace, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in command.Skip(1)) info.ArgumentList.Add(argument);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment[PistonLaunch.ExecutionDirectoryVariable] = launch.ExecutionDirectory;
        Evidence.WriteJson(Path.Combine(artifacts, "piston-launch.json"), new
        {
            command, daemon_assembly_sha256 = Evidence.Digest(File.ReadAllBytes(dll)),
            execution_directory = launch.ExecutionDirectory,
            policy = $"prebuilt isolated daemon; test-process parallelism {launch.Parallelism}; watching enabled; " +
                "per-trial admission directory with no budget.json (engine default budget)"
        });
        daemon = new Process { StartInfo = info };
        log = new StreamWriter(Path.Combine(artifacts, "piston-daemon.log")) { AutoFlush = true };
        try { daemon.Start(); }
        catch { log.Dispose(); daemon.Dispose(); throw; }
        logs = Task.WhenAll(CopyLog(daemon.StandardOutput), CopyLog(daemon.StandardError));
        mcp = new PistonMcpClient(new Uri($"http://localhost:{mcpPort}/"));
    }

    public async Task Prepare(string fingerprint, IReadOnlyDictionary<string, string> expected,
        CancellationToken cancellationToken)
    {
        var start = Evidence.NowNs;
        while (true)
        {
            EnsureRunning();
            try { await mcp.InitializeAsync(cancellationToken); break; }
            catch (HttpRequestException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
        var startup = await WaitForRun(run => Text(run, "trigger") == "Startup" && Terminal(run), cancellationToken);
        startupId = Text(startup, "runId") ?? throw new InvalidDataException("Piston startup has no run ID");
        File.WriteAllText(Path.Combine(artifacts, "piston-startup.json"), startup.ToJsonString());
        PistonRunOracle.Validate(startup.ToJsonString(), startupId, fingerprint, expected, solution);
        Evidence.WriteJson(Path.Combine(artifacts, "piston-setup.json"),
            new { elapsed_ms = (Evidence.NowNs - start) / 1e6, run_id = startupId, source_fingerprint = fingerprint });
    }

    public async Task<PistonExecution> Run(string fingerprint, CancellationToken cancellationToken)
    {
        var start = Evidence.NowNs;
        var samples = new List<ResourceSample>();
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var observation = Observe(samples, start, sampling.Token);
        try
        {
            // A forced request before the debounced watcher fires could be cancelled by that watcher.
            // Settling remains inside edit latency; no selective result or retry can satisfy the trial.
            var watched = await WaitForRun(run => Text(run, "trigger") == "FileChange" &&
                Text(run, "runId") != startupId && Terminal(run) &&
                Text(run["sourceBefore"] as JsonObject, "value") == fingerprint, cancellationToken);
            File.WriteAllText(Path.Combine(artifacts, "piston-file-change.json"), watched.ToJsonString());
            var response = await mcp.CallToolAsync("run_tests", cancellationToken: cancellationToken);
            File.WriteAllText(Path.Combine(artifacts, "piston-run-tests.txt"), response);
            var ids = Regex.Matches(response, @"(?m)^Run: ([0-9a-f]{32}) \|");
            if (ids.Count != 1) throw new InvalidDataException("Piston run_tests did not report exactly one run ID");
            var id = ids[0].Groups[1].Value;
            if (id == startupId || id == Text(watched, "runId"))
                throw new InvalidDataException("Piston reused a setup or selective run ID");
            var json = await mcp.CallToolAsync("get_test_run", new JsonObject { ["runId"] = id }, cancellationToken);
            File.WriteAllText(Path.Combine(artifacts, "piston-run.json"), json);
            EnsureRunning();
            sampling.Cancel();
            await observation;
            return new(id, json, (Evidence.NowNs - start) / 1e6, daemon.Id,
                samples.Count > 0 ? samples.Max(sample => sample.CpuSecondsAlive) : 0,
                samples.Count > 0 ? samples.Max(sample => sample.RssBytesAlive) : 0);
        }
        finally
        {
            sampling.Cancel();
            await observation;
            Evidence.WriteJson(Path.Combine(artifacts, "piston-edited.resources.json"), samples);
        }
    }

    private async Task Observe(List<ResourceSample> samples, long start, CancellationToken token)
    {
        try
        {
            while (true)
            {
                EnsureRunning();
                samples.Add(Processes.SampleOwned(daemon.Id, Evidence.NowNs - start));
                await Task.Delay(50, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private async Task<JsonObject> WaitForRun(Func<JsonObject, bool> predicate, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            EnsureRunning();
            try
            {
                if (JsonNode.Parse(await mcp.CallToolAsync("get_test_run",
                        new JsonObject { ["runId"] = "latest" }, token)) is not JsonObject run)
                    throw new InvalidDataException("Piston run document is not an object");
                if (predicate(run)) return run;
            }
            catch (InvalidOperationException error) when (
                error.Message.Contains("No test run has finished yet.", StringComparison.Ordinal))
            {
                // Startup has not published a terminal run yet.
            }
            await Task.Delay(25, token);
        }
    }

    private void EnsureRunning()
    {
        if (daemon.HasExited) throw new InvalidDataException($"Piston daemon exited with {daemon.ExitCode}; see piston-daemon.log");
    }

    private async Task CopyLog(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
            lock (log) log.WriteLine(line);
    }

    private static string? Text(JsonObject? node, string key) =>
        node?[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Terminal(JsonObject node) =>
        node["isTerminal"] is JsonValue value && value.TryGetValue<bool>(out var terminal) && terminal;

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            var exitedBeforeCleanup = daemon.HasExited;
            if (!exitedBeforeCleanup) Processes.TerminateOwned(daemon);
            await logs;
            Evidence.WriteJson(Path.Combine(artifacts, "piston-cleanup.json"),
                new { daemon_pid = daemon.Id, exited = daemon.HasExited, exit_code = daemon.ExitCode,
                    exited_before_cleanup = exitedBeforeCleanup });
            if (exitedBeforeCleanup)
                throw new InvalidOperationException($"Piston daemon exited before cleanup with {daemon.ExitCode}");
        }
        finally { mcp.Dispose(); log.Dispose(); daemon.Dispose(); }
    }
}
