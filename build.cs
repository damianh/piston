#!/usr/bin/env dotnet
// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

// Build script: `dotnet run build.cs -- [targets] [Bullseye options]`; `--list-targets` lists the targets.
// The smoke targets pack and install the tool, then run samples/Samples.slnx (MSTest, NUnit, xUnit v3 and TUnit,
// each with one passing, one deliberately failing and one skipped test) through `piston daemon` and `piston mcp`.

#:package Bullseye
#:package ModelContextProtocol.Core
#:package SimpleExec

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Bullseye.Targets;
using static SimpleExec.Command;

const string Solution = "Piston.slnx";
const string Samples = "samples/Samples.slnx";
const string Packages = "artifacts/packages";
const string ToolPath = ".tools";
var expected = new Counts(Total: 12, Passed: 4, Failed: 4, Skipped: 4);
const int ExpectedSuites = 4;
var smokeTimeout = TimeSpan.FromMinutes(10);

Directory.SetCurrentDirectory(
    AppContext.GetData("EntryPointFileDirectoryPath") as string ?? Directory.GetCurrentDirectory());
var tool = Path.GetFullPath(Path.Combine(ToolPath, OperatingSystem.IsWindows() ? "piston.exe" : "piston"));
var samples = Path.GetFullPath(Samples);

Target("build", "Build the solution (Release).",
    () => RunAsync("dotnet", ["build", Solution, "-c", "Release"]));

Target("test", "Run the solution's tests (Release).", dependsOn: ["build"],
    () => RunAsync("dotnet", ["test", Solution, "-c", "Release", "--no-build"]));

Target("check-formatting", "Verify the solution is formatted.",
    () => RunAsync("dotnet", ["format", Solution, "--verify-no-changes"]));

Target("pack", $"Pack the tool into {Packages} and check the package contents.", async () =>
{
    DeleteDirectory(Packages);
    await RunAsync("dotnet", ["pack", "src/Piston/Piston.csproj", "-c", "Release", "-o", Packages]);
    CheckPackageContents(Packages);
});

Target("check-package",
    $"Check the packed tool's contents in {Packages} (or the folder in PISTON_PACKAGES).",
    () => CheckPackageContents(Environment.GetEnvironmentVariable("PISTON_PACKAGES") is { Length: > 0 } dir
        ? dir
        : Packages));

Target("install-tool", $"Install the packed tool into {ToolPath}.", dependsOn: ["pack"], async () =>
{
    DeleteDirectory(ToolPath);
    // The package version does not change between local builds, so restore into a fresh packages folder
    // rather than reusing a stale copy from the global one.
    var nugetPackages = Path.GetFullPath("artifacts/nuget-packages");
    DeleteDirectory(nugetPackages);
    await RunAsync("dotnet", ["tool", "install", "Piston", "--tool-path", ToolPath, "--source", Packages],
        configureEnvironment: env => env["NUGET_PACKAGES"] = nugetPackages);
});

Target("smoke-daemon", "Run the samples through `piston daemon` and check the startup run's counts.",
    dependsOn: ["install-tool"], SmokeDaemonAsync);

Target("smoke-mcp", "Call `verify` on the samples through `piston mcp` (stdio) and check the verdict and counts.",
    dependsOn: ["install-tool"], SmokeMcpAsync);

Target("smoke", "Run both smoke tests.", dependsOn: ["smoke-daemon", "smoke-mcp"]);

Target("default", dependsOn: ["test", "check-formatting"]);

await RunTargetsAndExitAsync(args,
    messageOnly: ex => ex is SimpleExec.ExitCodeException or SmokeTestException or PackCheckException);

async Task SmokeDaemonAsync()
{
    var output = new StringBuilder();
    using var daemon = StartPiston(output, "daemon", samples, "--web-port", "5299", "--mcp-port", "5300");
    try
    {
        var (line, status, counts, suites) = await WaitForStartupRunAsync(daemon);
        Console.WriteLine($"Startup run: {line}");
        var mismatches = new List<string>();
        if (counts != expected)
        {
            mismatches.Add($"counts (expected {expected}, got {counts})");
        }

        if (suites != ExpectedSuites)
        {
            mismatches.Add($"suites (expected {ExpectedSuites}, got {suites})");
        }

        if (status != "Completed")
        {
            mismatches.Add($"status (expected Completed, got {status})");
        }

        if (mismatches.Count > 0)
        {
            throw new SmokeTestException($"Daemon smoke test failed; mismatched: {string.Join(", ", mismatches)}");
        }

        Console.WriteLine("Daemon smoke test passed.");
    }
    catch (Exception ex) when (ex is SmokeTestException or TimeoutException)
    {
        DumpDiagnostics("daemon output", output);
        throw new SmokeTestException(ex.Message);
    }
    finally
    {
        await StopDaemonAsync(daemon);
    }
}

async Task<(string Line, string Status, Counts Counts, int Suites)> WaitForStartupRunAsync(Process daemon)
{
    var deadline = DateTime.UtcNow + smokeTimeout;
    while (DateTime.UtcNow < deadline)
    {
        if (daemon.HasExited)
        {
            throw new SmokeTestException("The daemon exited before the startup run finished.");
        }

        var (status, _) = await ReadAsync(tool, ["status", samples], handleExitCode: _ => true);
        var latest = Regex.Match(status, @"^\s*Latest\s*:\s*(.+)$", RegexOptions.Multiline);
        var active = Regex.IsMatch(status, @"^\s*Active\s*:", RegexOptions.Multiline);
        var fields = latest.Success ? latest.Groups[1].Value.Split('|').Select(f => f.Trim()).ToArray() : [];
        if (fields.Length > 2 && fields[1] == "Startup" && !active)
        {
            var line = latest.Groups[1].Value.Trim();
            var counts = Regex.Match(line, @"(\d+) tests \((\d+) passed, (\d+) failed, (\d+) skipped\)");
            if (!counts.Success)
            {
                throw new SmokeTestException($"Unrecognized status line: {line}");
            }

            var suites = Regex.Match(status, @"^\s*Suites\s*:\s*(\d+)", RegexOptions.Multiline);
            return (line, fields[2],
                new Counts(Number(counts, 1), Number(counts, 2), Number(counts, 3), Number(counts, 4)),
                suites.Success ? Number(suites, 1) : -1);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
    }

    throw new TimeoutException($"Timed out after {smokeTimeout} waiting for the startup run.");
}

async Task StopDaemonAsync(Process daemon)
{
    if (daemon.HasExited)
    {
        return;
    }

    _ = await ReadAsync(tool, ["stop", samples], handleExitCode: _ => true);
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try
    {
        await daemon.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        daemon.Kill(entireProcessTree: true);
    }
}

async Task SmokeMcpAsync()
{
    var stderr = new StringBuilder();
    using var timeout = new CancellationTokenSource(smokeTimeout);
    string text;
    bool isError;
    try
    {
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "piston mcp",
            Command = tool,
            Arguments = ["mcp", samples],
            StandardErrorLines = line => Append(stderr, line),
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        var result = await client.CallToolAsync("verify", cancellationToken: timeout.Token);
        text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        isError = result.IsError == true;
    }
    catch (Exception ex) when (ex is not SmokeTestException)
    {
        DumpDiagnostics("piston mcp stderr", stderr);
        throw new SmokeTestException($"MCP smoke test failed: {ex.Message}");
    }

    Console.WriteLine(text);
    string[] required =
    [
        "Verdict: TestsFailed",
        "Status: Completed",
        "Scope: Full",
        $"Tests: {expected.Total} (Passed: {expected.Passed}, Failed: {expected.Failed}, Skipped: {expected.Skipped})",
        $"Failed tests ({expected.Failed}):",
    ];
    var missing = required.Where(r => !text.Contains(r, StringComparison.Ordinal)).ToList();
    if (isError || missing.Count > 0)
    {
        DumpDiagnostics("piston mcp stderr", stderr);
        throw new SmokeTestException(isError
            ? "MCP smoke test failed: verify returned a tool error."
            : $"MCP smoke test failed; verify output is missing: {string.Join("; ", missing)}");
    }

    Console.WriteLine("MCP smoke test passed.");
}

Process StartPiston(StringBuilder output, params string[] arguments)
{
    var info = new ProcessStartInfo(tool)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var argument in arguments)
    {
        info.ArgumentList.Add(argument);
    }

    Console.WriteLine($"> {tool} {string.Join(' ', arguments)}");
    var process = Process.Start(info) ?? throw new SmokeTestException($"Could not start {tool}.");
    process.OutputDataReceived += (_, e) => Append(output, e.Data);
    process.ErrorDataReceived += (_, e) => Append(output, e.Data);
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    return process;
}

void DumpDiagnostics(string title, StringBuilder output)
{
    lock (output)
    {
        Console.Error.WriteLine($"── {title} ──");
        Console.Error.WriteLine(output.ToString());
    }

    var diagnostics = Path.Combine(Path.GetDirectoryName(samples)!, ".piston", "diagnostics.log");
    if (File.Exists(diagnostics))
    {
        Console.Error.WriteLine("── .piston/diagnostics.log ──");
        Console.Error.WriteLine(File.ReadAllText(diagnostics));
    }
}

static void Append(StringBuilder output, string? line)
{
    if (line is null)
    {
        return;
    }

    lock (output)
    {
        _ = output.AppendLine(line);
    }
}

static int Number(Match match, int group) => int.Parse(match.Groups[group].Value);

// Piston.Web reaches the tool only as static files under wwwroot/; nothing else from it belongs in the package.
static void CheckPackageContents(string directory)
{
    var package = Directory.GetFiles(directory, "Piston.*.nupkg").Single();
    using var archive = ZipFile.OpenRead(package);
    var entries = archive.Entries.Select(e => e.FullName).ToList();
    var unexpected = entries
        .Where(e => !e.Contains("/wwwroot/", StringComparison.Ordinal))
        .Where(e => Path.GetFileName(e) is var name
            && (name.StartsWith("Piston.Web.", StringComparison.Ordinal)
                || name is "package.json" or "package-lock.json"))
        .ToList();
    var problems = new List<string>();
    if (unexpected.Count > 0)
    {
        problems.Add($"unexpected Piston.Web files: {string.Join(", ", unexpected)}");
    }

    if (!entries.Any(e => e.EndsWith("/wwwroot/index.html", StringComparison.Ordinal)))
    {
        problems.Add("the dashboard (wwwroot/index.html) is missing");
    }

    if (problems.Count > 0)
    {
        throw new PackCheckException($"{Path.GetFileName(package)}: {string.Join("; ", problems)}");
    }

    Console.WriteLine($"Package contents check passed ({Path.GetFileName(package)}).");
}

static void DeleteDirectory(string path)
{
    if (Directory.Exists(path))
    {
        Directory.Delete(path, recursive: true);
    }
}

internal sealed record Counts(int Total, int Passed, int Failed, int Skipped);

internal sealed class SmokeTestException(string message) : Exception(message);

internal sealed class PackCheckException(string message) : Exception(message);
