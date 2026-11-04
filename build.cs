#!/usr/bin/env dotnet
// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

// Build script: `dotnet run build.cs -- [targets] [Bullseye options]`; `--list-targets` lists the targets.
// The smoke targets pack and install the tool, then run samples/Samples.slnx (MSTest, NUnit, xUnit v3 and TUnit,
// each with one passing, one deliberately failing and one skipped test) through `piston daemon` and `piston mcp`,
// and install the harness assets with `piston init` into temporary locations.

#:package Bullseye
#:package ModelContextProtocol.Core
#:package SimpleExec
#:package JsonSchema.Net

using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using Json.Schema;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Bullseye.Targets;
using static SimpleExec.Command;

const string Solution = "Piston.slnx";
const string Samples = "samples/Samples.slnx";
const string Packages = "artifacts/packages";
const string PackageId = "DamianH.Piston";
const string ToolPath = ".tools";
var expected = new Counts(Total: 12, Passed: 4, Failed: 4, Skipped: 4);
const int ExpectedSuites = 4;
var smokeTimeout = TimeSpan.FromMinutes(10);

var invocationDirectory = Directory.GetCurrentDirectory();
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
    // Restore into a fresh packages folder rather than reusing a stale local build.
    var nugetPackages = Path.GetFullPath("artifacts/nuget-packages");
    DeleteDirectory(nugetPackages);
    var package = Directory.GetFiles(Packages, $"{PackageId}.*.nupkg").Single();
    await RunAsync("dotnet", ["tool", "install", PackageId, "--version", ReadPackageVersion(package),
            "--tool-path", ToolPath, "--source", Packages],
        configureEnvironment: env => env["NUGET_PACKAGES"] = nugetPackages);
});

Target("smoke-daemon", "Run the samples through `piston daemon` and check the startup run's counts.",
    dependsOn: ["install-tool"], () => SmokeDaemonAsync());

Target("smoke-mcp", "Call `verify` on the samples through `piston mcp` (stdio) and check the verdict and counts.",
    dependsOn: ["install-tool"], () => SmokeMcpAsync());

Target("smoke-distribution", "Check dnx and live upgrades in isolated tool stores.",
    SmokeDistributionAsync);

Target("smoke-public", "Wait for the released NuGet package and verify public-feed dnx MCP.",
    SmokePublicAsync);

Target("smoke-init", "Install the harness assets with `piston init` into temporary locations and check them.",
    dependsOn: ["install-tool"], SmokeInitAsync);

Target("smoke", "Run the smoke tests.", dependsOn: ["smoke-daemon", "smoke-mcp", "smoke-init"]);

Target("test-release", "Run the release tooling's self-tests.", ReleaseTests.RunAsync);

foreach (var (command, description) in new[]
         {
             ("context", "Resolve the release workflow context (GITHUB_* and RELEASE_MANUAL_PUBLISH)."),
             ("prepare", "Verify release history and reserve the next calendar version tag."),
             ("reconcile", "Decide whether the reserved package still needs to be pushed to NuGet."),
             ("verify-package", "Verify the public NuGet package matches the release artifact."),
             ("github-release", "Create or verify the stable GitHub Release and its package asset."),
         })
{
    Target($"release-{command}", description, () => ReleaseTooling.RunCommandAsync(
        command, Environment.GetEnvironmentVariable, invocationDirectory, Console.Out));
}

Target("smoke-tool", "Smoke-test an installed tool (SMOKE_TOOL, optional SMOKE_VERSION and SMOKE_VERSION_ONLY=true).",
    () => SmokeToolAsync(
        Environment.GetEnvironmentVariable("SMOKE_TOOL") is { Length: > 0 } smokeTool
            ? smokeTool
            : throw new SmokeTestException("SMOKE_TOOL must name the installed tool."),
        Environment.GetEnvironmentVariable("SMOKE_VERSION") is { Length: > 0 } smokeVersion ? smokeVersion : null,
        Environment.GetEnvironmentVariable("SMOKE_VERSION_ONLY") == "true"));

Target("default", dependsOn: ["test", "check-formatting"]);

await RunTargetsAndExitAsync(args,
    messageOnly: ex => ex is SimpleExec.ExitCodeException or SmokeTestException or PackCheckException or ReleaseException
        or ReleaseTestFailedException);

async Task SmokeDaemonAsync(string? expectedVersion = null)
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

        if (expectedVersion is not null)
        {
            var (versionStatus, _) = await ReadAsync(tool, ["status", samples]);
            if (!versionStatus.Contains(expectedVersion, StringComparison.Ordinal))
            {
                throw new SmokeTestException($"Restarted daemon is not {expectedVersion}: {versionStatus}");
            }

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var page = await http.GetStringAsync("http://localhost:5299/");
            if (!page.Contains("_framework/", StringComparison.Ordinal))
            {
                throw new SmokeTestException("Restarted daemon has no dashboard assets.");
            }
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
        var latest = MyRegex().Match(status);
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
        await daemon.WaitForExitAsync();
    }
}

async Task SmokeMcpAsync(string? command = null, string[]? arguments = null,
    Dictionary<string, string?>? environment = null, string? expectedServerVersion = null)
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
            Command = command ?? tool,
            Arguments = arguments ?? ["mcp", samples],
            EnvironmentVariables = environment,
            StandardErrorLines = line => Append(stderr, line),
        });
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
        if (expectedServerVersion is not null && client.ServerInfo?.Version != expectedServerVersion)
        {
            throw new SmokeTestException($"MCP server version is {client.ServerInfo?.Version ?? "unknown"}, expected {expectedServerVersion}.");
        }

        _ = await client.ListToolsAsync(cancellationToken: timeout.Token);
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

async Task SmokeDistributionAsync()
{
    const string oldVersion = "2026.101.1-dev";
    const string newVersion = "2026.101.2";
    var root = Path.Combine(Path.GetTempPath(), $"piston-distribution-{Guid.NewGuid():N}");
    var feed = Path.Combine(root, "feed");
    var originalTool = tool;
    var originalSamples = samples;
    var environment = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_HOME"] = Path.Combine(root, "cli-home"),
        ["NUGET_PACKAGES"] = Path.Combine(root, "nuget-packages"),
        ["DOTNET_NOLOGO"] = "true",
        ["MSBUILDDISABLENODEREUSE"] = "1",
    };
    void Configure(IDictionary<string, string?> env)
    {
        foreach (var (key, value) in environment)
        {
            env[key] = value;
        }
    }

    async Task ToolCommandAsync(string operation, string version)
    {
        var (output, error) = await ReadAsync("dotnet",
            ["tool", operation, PackageId, "--version", version, "--tool-path", Path.GetDirectoryName(tool)!,
                    "--source", feed], configureEnvironment: Configure);
        Console.WriteLine(output + error);
    }

    async Task ProbeUpdateAsync(string mode)
    {
        var exitCode = 0;
        var (output, error) = await ReadAsync("dotnet",
            ["tool", "update", PackageId, "--version", newVersion, "--tool-path", Path.GetDirectoryName(tool)!,
                    "--source", feed], configureEnvironment: Configure, handleExitCode: code =>
            {
                exitCode = code;
                return true;
            });
        Console.WriteLine($"Live {mode} update:\n{output}{error}");
        if (exitCode != 0)
        {
            if (!OperatingSystem.IsWindows()
                || !Regex.IsMatch(output + error,
                    @"being used by another process|Access to the path .*\.store.* is denied",
                    RegexOptions.IgnoreCase))
            {
                throw new SmokeTestException($"Unexpected live {mode} update failure: {output}{error}");
            }

            Console.WriteLine($"OBSERVATION: Windows live {mode} update encountered a tool-store lock.");
        }
    }

    static bool HasVersion(string output, string expectedVersion) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Trim().Split('+', 2)[0] == expectedVersion);

    async Task CheckVersionAsync(string expectedVersion)
    {
        var (version, error) = await ReadAsync(tool, ["--version"]);
        if (!HasVersion(version, expectedVersion))
        {
            throw new SmokeTestException($"CLI is not {expectedVersion}: {version}{error}");
        }
    }

    _ = Directory.CreateDirectory(feed);
    try
    {
        samples = CreateSmokeFixture(root, originalSamples);

        foreach (var version in new[] { oldVersion, newVersion })
        {
            await RunAsync("dotnet", ["pack", "src/Piston/Piston.csproj", "-c", "Release",
                    $"-p:Version={version}", "-o", feed]);
        }

        foreach (var package in Directory.GetFiles(feed, "*.nupkg"))
        {
            var check = Path.Combine(root, Path.GetFileNameWithoutExtension(package));
            _ = Directory.CreateDirectory(check);
            File.Copy(package, Path.Combine(check, Path.GetFileName(package)));
            CheckPackageContents(check);
        }

        var (help, helpError) = await ReadAsync("dotnet",
            ["dnx", $"{PackageId}@{oldVersion}", "--source", feed, "--", "--help"],
            configureEnvironment: Configure);
        if (!help.Contains("mcp", StringComparison.Ordinal) || !help.Contains("daemon", StringComparison.Ordinal))
        {
            throw new SmokeTestException($"dnx did not forward --help: {help}{helpError}");
        }

        foreach (var version in new[] { oldVersion, newVersion })
        {
            var (actual, error) = await ReadAsync("dotnet",
                ["dnx", $"{PackageId}@{version}", "--source", feed, "--", "--version"],
                configureEnvironment: Configure);
            if (!HasVersion(actual, version))
            {
                throw new SmokeTestException($"dnx CLI is not {version}: {actual}{error}");
            }
        }

        await SmokeMcpAsync("dotnet",
            ["dnx", $"{PackageId}@{oldVersion}", "--source", feed, "--", "mcp", samples], environment,
            expectedServerVersion: oldVersion);

        tool = Path.Combine(root, "mcp-tool", OperatingSystem.IsWindows() ? "piston.exe" : "piston");
        await ToolCommandAsync("install", oldVersion);
        await CheckVersionAsync(oldVersion);
        using (var timeout = new CancellationTokenSource(smokeTimeout))
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = tool,
                Arguments = ["mcp", samples],
                EnvironmentVariables = environment,
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token);
            _ = await client.ListToolsAsync(cancellationToken: timeout.Token);
            var baseline = await client.CallToolAsync("verify", cancellationToken: timeout.Token);
            var baselineText = string.Join('\n', baseline.Content.OfType<TextContentBlock>().Select(c => c.Text));
            if (baseline.IsError == true || !baselineText.Contains("Verdict: TestsFailed", StringComparison.Ordinal)
                || !baselineText.Contains("Tests: 12 (Passed: 4, Failed: 4, Skipped: 4)", StringComparison.Ordinal)
                || client.ServerInfo?.Version != oldVersion)
            {
                throw new SmokeTestException("Old MCP version failed before updating.");
            }

            await ProbeUpdateAsync("mcp");
            try
            {
                var result = await client.CallToolAsync("run_tests", cancellationToken: timeout.Token);
                var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Console.WriteLine($"OBSERVATION: live MCP forced run after update: isError={result.IsError == true}\n{text}");
            }
            catch (Exception ex) when (ex is IOException or McpException)
            {
                Console.WriteLine($"OBSERVATION: live MCP could not run tests after update: {ex.Message}");
            }
        }

        await ToolCommandAsync("update", newVersion);
        await CheckVersionAsync(newVersion);
        await SmokeMcpAsync(environment: environment, expectedServerVersion: newVersion);

        tool = Path.Combine(root, "daemon-tool", OperatingSystem.IsWindows() ? "piston.exe" : "piston");
        await ToolCommandAsync("install", oldVersion);
        await CheckVersionAsync(oldVersion);
        var daemonOutput = new StringBuilder();
        using (var daemon = StartPiston(daemonOutput, "daemon", samples, "--web-port", "5299", "--mcp-port", "5300"))
        {
            try
            {
                _ = await WaitForStartupRunAsync(daemon);
                await ProbeUpdateAsync("daemon");
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var response = await http.GetAsync("http://localhost:5299/");
                Console.WriteLine($"OBSERVATION: live daemon dashboard after update: {response.StatusCode}");
                var (status, _) = await ReadAsync(tool, ["status", samples]);
                if (!status.Contains(oldVersion, StringComparison.Ordinal))
                {
                    throw new SmokeTestException("Live daemon did not retain the old process version.");
                }
            }
            finally
            {
                await StopDaemonAsync(daemon);
            }
        }

        await ToolCommandAsync("update", newVersion);
        await CheckVersionAsync(newVersion);
        await SmokeDaemonAsync(newVersion);
        Console.WriteLine($"PASS: dnx MCP and stop/update/restart on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
    }
    finally
    {
        tool = originalTool;
        samples = originalSamples;
        DeleteDirectory(root);
    }
}

async Task SmokePublicAsync()
{
    var directory = Environment.GetEnvironmentVariable("PISTON_PACKAGES") ?? Packages;
    var source = Directory.GetFiles(directory, $"{PackageId}.*.nupkg").Single();
    var version = ReadPackageVersion(source);
    if (!Regex.IsMatch(version, @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$"))
    {
        throw new SmokeTestException($"Invalid public package version: {version}");
    }

    var root = Path.Combine(Path.GetTempPath(), $"piston-public-{Guid.NewGuid():N}");
    var originalSamples = samples;
    _ = Directory.CreateDirectory(root);
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var packageId = PackageId.ToLowerInvariant();
        var url = $"https://api.nuget.org/v3-flatcontainer/{packageId}/{version.ToLowerInvariant()}/{packageId}.{version.ToLowerInvariant()}.nupkg";
        var destination = Path.Combine(root, $"{PackageId}.{version}.nupkg");
        for (var attempt = 1; ; attempt++)
        {
            using var response = await http.GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                await File.WriteAllBytesAsync(destination, await response.Content.ReadAsByteArrayAsync());
                break;
            }

            if (attempt == 30 || response.StatusCode != System.Net.HttpStatusCode.NotFound)
            {
                throw new SmokeTestException($"Public package unavailable after {attempt} checks: HTTP {response.StatusCode}");
            }

            Console.WriteLine($"Waiting for NuGet indexing ({attempt}/30).");
            await Task.Delay(TimeSpan.FromSeconds(20));
        }

        CheckPackageContents(root, expectedVersion: version);
        samples = CreateSmokeFixture(root, originalSamples);
        await SmokeMcpAsync("dotnet",
            ["dnx", $"{PackageId}@{version}", "--source", "https://api.nuget.org/v3/index.json", "--", "mcp", samples],
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_HOME"] = Path.Combine(root, "cli-home"),
                ["NUGET_PACKAGES"] = Path.Combine(root, "nuget-packages"),
                ["DOTNET_NOLOGO"] = "true",
            },
            expectedServerVersion: version);
        Console.WriteLine($"PASS: NuGet.org metadata and dnx MCP for {PackageId} {version}.");
    }
    finally
    {
        samples = originalSamples;
        DeleteDirectory(root);
    }
}

static string CreateSmokeFixture(string root, string sourceSolution)
{
    var fixture = Path.Combine(root, "fixture");
    var sourceDirectory = Path.GetDirectoryName(sourceSolution)!;
    _ = Directory.CreateDirectory(fixture);
    foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
        .Where(f => Path.GetExtension(f) is ".cs" or ".csproj" or ".props" or ".slnx")
        .Where(f => !f.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".piston")))
    {
        var destination = Path.Combine(fixture, Path.GetRelativePath(sourceDirectory, file));
        _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(file, destination);
    }

    File.Copy(Path.Combine(sourceDirectory, ".editorconfig"), Path.Combine(fixture, ".editorconfig"));
    return Path.Combine(fixture, Path.GetFileName(sourceSolution));
}

// The packed tool must carry its own assets: install them for Copilot (user scope) and Claude Code (repo scope)
// into temporary locations, check the files, then check a second run changes nothing.
async Task SmokeInitAsync()
{
    var root = Path.Combine(Path.GetTempPath(), $"piston-smoke-init-{Guid.NewGuid():N}");
    var copilotHome = Path.Combine(root, "copilot");
    var repo = Path.Combine(root, "repo");
    _ = Directory.CreateDirectory(repo);
    try
    {
        async Task<string> InitAsync(params string[] arguments)
        {
            Console.WriteLine($"> {tool} {string.Join(' ', arguments)}");
            var (output, error) = await ReadAsync(tool, arguments, workingDirectory: repo,
                configureEnvironment: env =>
                {
                    env["COPILOT_HOME"] = copilotHome;
                    env["CLAUDE_CONFIG_DIR"] = Path.Combine(root, "claude");
                });
            Console.WriteLine(output + error);
            return output;
        }

        string[] copilot = ["init", "copilot", "--write"];
        string[] claude = ["init", "claude", "--write", "--scope", "repo"];
        _ = await InitAsync(copilot);
        _ = await InitAsync(claude);

        string[] files =
        [
            Path.Combine(copilotHome, "mcp-config.json"),
            Path.Combine(copilotHome, "skills", "piston", "SKILL.md"),
            Path.Combine(copilotHome, "extensions", "piston-dashboard", "extension.mjs"),
            Path.Combine(copilotHome, "extensions", "piston-dashboard", "shell.html"),
            Path.Combine(repo, ".mcp.json"),
            Path.Combine(repo, ".claude", "skills", "piston", "SKILL.md"),
        ];
        var problems = files.Where(f => !File.Exists(f)).Select(f => $"missing {f}").ToList();
        foreach (var config in files.Where(f => f.EndsWith("json", StringComparison.Ordinal) && File.Exists(f)))
        {
            if (!File.ReadAllText(config).Contains("\"piston\"", StringComparison.Ordinal))
            {
                problems.Add($"{config} has no piston server");
            }
        }

        foreach (var arguments in new[] { copilot, claude })
        {
            var rerun = await InitAsync(arguments);
            if (!rerun.Contains("unchanged", StringComparison.Ordinal) || rerun.Contains("created", StringComparison.Ordinal))
            {
                problems.Add($"`piston {string.Join(' ', arguments)}` was not idempotent");
            }
        }

        if (problems.Count > 0)
        {
            throw new SmokeTestException($"Init smoke test failed: {string.Join("; ", problems)}");
        }
    }
    finally
    {
        DeleteDirectory(root);
    }

    Console.WriteLine("Init smoke test passed.");
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

async Task SmokeToolAsync(string toolPath, string? expectedVersion, bool versionOnly)
{
    toolPath = Path.GetFullPath(toolPath, invocationDirectory);
    await RunAsync(toolPath, ["--help"]);
    if (expectedVersion is not null)
    {
        var (versionOutput, _) = await ReadAsync(toolPath, ["--version"]);
        if (versionOutput.Trim().Split('+', 2)[0] != expectedVersion)
        {
            throw new SmokeTestException($"Installed tool reports {versionOutput.Trim()}, expected {expectedVersion}.");
        }
    }

    var directory = Directory.CreateTempSubdirectory("piston-tool-smoke-").FullName;
    var output = new StringBuilder();
    Process? daemon = null;
    var root = Directory.CreateDirectory(Path.Combine(directory, "solution")).FullName;
    var solution = Path.Combine(root, "Smoke.slnx");
    var pipe = $"piston-smoke-{Guid.NewGuid():N}";
    try
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Smoke.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <IsTestProject>true</IsTestProject>
                <IsPackable>false</IsPackable>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.11.0" />
                <PackageReference Include="xunit" Version="2.9.3" />
                <PackageReference Include="xunit.runner.visualstudio" Version="4.0.1" />
                <PackageReference Include="coverlet.collector" Version="10.1.0" />
              </ItemGroup>
            </Project>

            """);
        await File.WriteAllTextAsync(Path.Combine(root, "SmokeTests.cs"),
            "public class SmokeTests { [Xunit.Fact] public void Passes() { Xunit.Assert.True(true); } }\n");
        await File.WriteAllTextAsync(solution, "<Solution><Project Path=\"Smoke.csproj\" /></Solution>\n");
        using (var restore = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            await RunAsync("dotnet", ["restore", solution], workingDirectory: root, ct: restore.Token);
        }

        var webPort = FreePort();
        var mcpPort = FreePort();
        while (mcpPort == webPort)
        {
            mcpPort = FreePort();
        }

        var webUrl = $"http://localhost:{webPort}";
        var info = new ProcessStartInfo(toolPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = root,
        };
        foreach (var argument in new[]
                 {
                     "daemon", solution, "--web-port", $"{webPort}", "--mcp-port", $"{mcpPort}", "--pipe-name", pipe, "--coverage",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        daemon = Process.Start(info) ?? throw new SmokeTestException($"Could not start {toolPath}.");
        daemon.OutputDataReceived += (_, e) => Append(output, e.Data);
        daemon.ErrorDataReceived += (_, e) => Append(output, e.Data);
        daemon.BeginOutputReadLine();
        daemon.BeginErrorReadLine();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
        string page;
        while (true)
        {
            if (daemon.HasExited)
            {
                throw new SmokeTestException($"Daemon exited with {daemon.ExitCode}.");
            }

            try
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                page = await http.GetStringAsync(webUrl, attempt.Token);
                using var probe = new System.Net.Sockets.TcpClient();
                await probe.ConnectAsync("localhost", mcpPort, attempt.Token);
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or System.Net.Sockets.SocketException)
            {
                if (DateTime.UtcNow >= deadline)
                {
                    throw new SmokeTestException("Installed tool did not start its web and MCP servers.");
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250));
            }
        }

        if (!versionOnly)
        {
            var framework = Regex.Match(page, "src=\"(_framework/blazor\\.webassembly[^\"]*\\.js)\"");
            if (!page.Contains("_framework/", StringComparison.Ordinal) || !framework.Success)
            {
                throw new SmokeTestException($"Dashboard page has no Blazor framework script:\n{page}");
            }

            using var script = await http.GetAsync($"{webUrl}/{framework.Groups[1].Value}");
            var body = await script.Content.ReadAsByteArrayAsync();
            if (script.StatusCode != System.Net.HttpStatusCode.OK || body.Length <= 1000
                || script.Content.Headers.ContentType?.MediaType?.Contains("javascript", StringComparison.Ordinal) != true)
            {
                throw new SmokeTestException(
                    $"Blazor framework script is invalid: {(int)script.StatusCode}, {body.Length} bytes, {script.Content.Headers.ContentType}.");
            }
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = "piston-package-smoke",
            Endpoint = new Uri($"http://localhost:{mcpPort}"),
            TransportMode = HttpTransportMode.StreamableHttp,
        });
        await using (var client = await McpClient.CreateAsync(transport, cancellationToken: timeout.Token))
        {
            if (expectedVersion is not null && client.ServerInfo?.Version != expectedVersion)
            {
                throw new SmokeTestException($"MCP server version is {client.ServerInfo?.Version ?? "unknown"}, expected {expectedVersion}.");
            }

            if (!versionOnly)
            {
                string[] expectedTools =
                [
                    "clear_results", "export_coverage", "get_changed_line_coverage", "get_coverage", "get_test_history",
                    "get_test_result", "get_test_results", "get_test_run", "plan_affected_tests", "run_affected_tests",
                    "run_tests", "set_test_filter", "verify",
                ];
                var names = (await client.ListToolsAsync(cancellationToken: timeout.Token)).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
                if (!names.SequenceEqual(expectedTools))
                {
                    throw new SmokeTestException($"Unexpected MCP tools: {string.Join(", ", names)}");
                }

                await CallAsync("verify", "Verdict: Passed", "Tests: 1 (Passed: 1, Failed: 0, Skipped: 0)");
                await CallAsync("run_tests", "Passed: 1, Failed: 0", "Phase: Watching");

                var header = new byte[16];
                await using (var database = new FileStream(
                                 Path.Combine(root, ".piston", "piston.db"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    await database.ReadExactlyAsync(header);
                }

                if (!header.AsSpan().SequenceEqual("SQLite format 3\0"u8))
                {
                    throw new SmokeTestException("The coverage store is not a SQLite database.");
                }
            }

            async Task CallAsync(string name, params string[] required)
            {
                var result = await client.CallToolAsync(name, cancellationToken: timeout.Token);
                var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                var missing = required.Where(r => !text.Contains(r, StringComparison.Ordinal)).ToList();
                if (result.IsError == true || missing.Count > 0)
                {
                    throw new SmokeTestException($"MCP {name} failed (missing: {string.Join("; ", missing)}):\n{text}");
                }
            }
        }

        using (var statusTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            var (status, _) = await ReadAsync(toolPath, ["status", solution, "--pipe-name", pipe], ct: statusTimeout.Token);
            if (!status.Contains("Daemon running.", StringComparison.Ordinal)
                || (expectedVersion is not null && !status.Contains(expectedVersion, StringComparison.Ordinal)))
            {
                throw new SmokeTestException($"Unexpected daemon status:\n{status}");
            }
        }

        Console.WriteLine(versionOnly
            ? "PASS: installed CLI, daemon and HTTP MCP version"
            : "PASS: installed CLI, bundled web/framework assets, HTTP MCP tool surface, one passing test, "
              + "native SQLite coverage store and named-pipe status");
    }
    catch (Exception ex)
    {
        DumpDiagnostics("daemon output", output);
        var diagnostics = Path.Combine(root, ".piston", "diagnostics.log");
        if (File.Exists(diagnostics))
        {
            Console.Error.WriteLine(await File.ReadAllTextAsync(diagnostics));
        }

        throw ex is SmokeTestException ? ex : new SmokeTestException($"Tool smoke test failed: {ex.Message}");
    }
    finally
    {
        if (daemon is not null)
        {
            if (!daemon.HasExited)
            {
                _ = await ReadAsync(toolPath, ["stop", solution, "--pipe-name", pipe], handleExitCode: _ => true);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    await daemon.WaitForExitAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    daemon.Kill(entireProcessTree: true);
                    await daemon.WaitForExitAsync();
                }
            }

            daemon.Dispose();
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
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

static string ReadPackageVersion(string packagePath)
{
    using var package = ZipFile.OpenRead(packagePath);
    using var stream = package.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
    return XDocument.Load(stream).Descendants().Single(e => e.Name.LocalName == "version").Value;
}

// Piston.Web reaches the tool only as static files under wwwroot/; nothing else from it belongs in the package.
static void CheckPackageContents(string directory, string? expectedVersion = null)
{
    var package = Directory.GetFiles(directory, $"{PackageId}.*.nupkg").Single();
    using var archive = ZipFile.OpenRead(package);
    var entries = archive.Entries.Select(e => e.FullName).ToList();
    var unexpected = entries
        .Where(e => !e.Contains("/wwwroot/", StringComparison.Ordinal))
        .Where(e => Path.GetFileName(e) is var name
            && (name.StartsWith("Piston.Web.", StringComparison.Ordinal)
                || name is "package.json" or "package-lock.json"))
        .ToList();
    var problems = new List<string>();
    var nuspec = archive.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
    using var nuspecStream = nuspec.Open();
    var specification = XDocument.Load(nuspecStream);
    var ns = specification.Root!.Name.Namespace;
    var metadata = specification.Root.Element(ns + "metadata")!;
    var version = metadata.Element(ns + "version")!.Value;
    if (metadata.Element(ns + "id")?.Value != PackageId)
    {
        problems.Add($"package ID must be {PackageId}");
    }
    if (expectedVersion is not null && version != expectedVersion)
    {
        problems.Add($"expected package version {expectedVersion}, got {version}");
    }
    var types = metadata.Element(ns + "packageTypes")!.Elements(ns + "packageType")
        .Select(e => (string?)e.Attribute("name")).ToHashSet();
    if (!types.SetEquals(["DotnetTool", "McpServer"]))
    {
        problems.Add("package types must be DotnetTool and McpServer");
    }

    var server = archive.GetEntry(".mcp/server.json")
        ?? throw new PackCheckException("Missing .mcp/server.json");
    var settings = archive.Entries.Single(e => e.FullName.EndsWith("/DotnetToolSettings.xml", StringComparison.Ordinal));
    using var settingsStream = settings.Open();
    var command = XDocument.Load(settingsStream).Descendants("Command").Single();
    if ((string?)command.Attribute("Name") != "piston"
        || (string?)command.Attribute("EntryPoint") != "Piston.dll")
    {
        problems.Add("tool command must be piston with entry point Piston.dll");
    }
    using var reader = new StreamReader(server.Open());
    var serverJson = reader.ReadToEnd();
    using var document = JsonDocument.Parse(serverJson);
    var json = JsonNode.Parse(serverJson)!;
    const string schemaUrl = "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var schema = JsonSchema.FromText(
        http.GetStringAsync(schemaUrl).GetAwaiter().GetResult(),
        new BuildOptions { SchemaRegistry = new SchemaRegistry() });
    if (!schema.Evaluate(document.RootElement).IsValid)
    {
        problems.Add("server.json does not conform to the pinned MCP schema");
    }

    var serverPackage = json["packages"]!.AsArray().Single()!;
    if (json["$schema"]!.GetValue<string>() != schemaUrl
        || json["name"]!.GetValue<string>() != "io.github.damianh/piston"
        || json["version"]!.GetValue<string>() != version
        || serverPackage["version"]!.GetValue<string>() != version
        || serverPackage["identifier"]!.GetValue<string>() != PackageId
        || serverPackage["registryType"]!.GetValue<string>() != "nuget"
        || serverPackage["registryBaseUrl"]!.GetValue<string>() != "https://api.nuget.org/v3/index.json"
        || serverPackage["runtimeHint"]!.GetValue<string>() != "dnx"
        || serverPackage["transport"]!["type"]!.GetValue<string>() != "stdio"
        || serverPackage["packageArguments"]!.AsArray().Single()!["value"]!.GetValue<string>() != "mcp")
    {
        problems.Add("MCP metadata does not match the tool version or stdio invocation");
    }

    using var readme = new StreamReader((archive.GetEntry("README.md")
        ?? throw new PackCheckException("Missing package README.md")).Open());
    if (!readme.ReadToEnd().Contains("mcp-name: io.github.damianh/piston", StringComparison.Ordinal))
    {
        problems.Add("package README is missing its MCP name");
    }
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

partial class Program
{
    [GeneratedRegex(@"^\s*Latest\s*:\s*(.+)$", RegexOptions.Multiline)]
    private static partial Regex MyRegex();
}
// ── Release tooling ──────────────────────────────────────────────────────────────────────────────────────────────
// Calendar versions are YYYY.MDD.N[-dev]; stable and dev releases share the daily counter N.
internal sealed record ReleaseVersion
{
    public const int MaxCounter = 65534;

    private static readonly Regex CalendarPrefix = new(@"^v[0-9]{4,}\.", RegexOptions.CultureInvariant);
    private static readonly Regex CalendarTag =
        new(@"^v([1-9][0-9]{3})\.([1-9][0-9]{2,3})\.([1-9][0-9]*)(-dev)?\z", RegexOptions.CultureInvariant);

    public ReleaseVersion(DateOnly day, int counter, bool dev)
    {
        if (day.Year < 1000)
        {
            throw new ReleaseException("The release year must have four digits.");
        }

        if (counter is < 1 or > MaxCounter)
        {
            throw new ReleaseException($"The daily release counter must be between 1 and {MaxCounter}.");
        }

        (Day, Counter, Dev) = (day, counter, dev);
    }

    public DateOnly Day { get; }

    public int Counter { get; }

    public bool Dev { get; }

    public string Version => string.Create(CultureInfo.InvariantCulture,
        $"{Day.Year}.{(Day.Month * 100) + Day.Day}.{Counter}{(Dev ? "-dev" : "")}");

    public string Tag => $"v{Version}";

    // Returns null for tags outside the calendar namespace; rejects malformed calendar tags.
    public static ReleaseVersion? ParseTag(string tag)
    {
        var match = CalendarTag.Match(tag);
        if (!match.Success)
        {
            return CalendarPrefix.IsMatch(tag) ? throw new ReleaseException($"Invalid calendar release tag: {tag}") : null;
        }

        ReleaseVersion version;
        try
        {
            var monthDay = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var counter))
            {
                throw new ReleaseException($"The daily release counter must be between 1 and {MaxCounter}.");
            }

            version = new ReleaseVersion(
                new DateOnly(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), monthDay / 100, monthDay % 100),
                counter,
                match.Groups[4].Success);
        }
        catch (Exception ex) when (ex is ReleaseException or ArgumentOutOfRangeException)
        {
            throw new ReleaseException($"Invalid calendar release tag: {tag}: {ex.Message}", ex);
        }

        return version.Tag == tag ? version : throw new ReleaseException($"Noncanonical calendar release tag: {tag}");
    }

    public static ReleaseVersion Allocate(DateOnly day, IEnumerable<string> tags, bool dev)
    {
        var counters = new Dictionary<(DateOnly Day, int Counter), string>();
        foreach (var tag in tags)
        {
            if (ParseTag(tag) is not { } version)
            {
                continue;
            }

            var key = (version.Day, version.Counter);
            if (counters.TryGetValue(key, out var existing) && existing != tag)
            {
                throw new ReleaseException($"Calendar release counter is reused by {existing} and {tag}.");
            }

            counters[key] = tag;
        }

        var last = counters.Keys.Where(k => k.Day == day).Select(k => k.Counter).DefaultIfEmpty(0).Max();
        return new ReleaseVersion(day, last + 1, dev);
    }
}

internal sealed class ReleaseException(string message, Exception? inner = null) : Exception(message, inner);

internal sealed record RetiredReservation(long RunId, string SourceSha, string PackageId, string Reason);

internal sealed record Reservation(
    ReleaseVersion Version, long RunId, string SourceSha, bool HasVerifiedHistory, string? VerifiedThrough);

internal sealed record ReleaseContext(string SourceSha, string Kind, bool Publish);

internal delegate Task<string> CommandRunner(string fileName, IReadOnlyList<string> arguments, string? workingDirectory);

// Reserves and verifies immutable releases for GitHub Actions. HTTP, the clock and external commands are seams for
// the release tests (`test-release`).
internal sealed partial class ReleaseTooling
{
    public const string PackageId = "DamianH.Piston";
    public const string ReleaseWorkflow = ".github/workflows/release.yml";

    public static readonly IReadOnlyDictionary<string, RetiredReservation> DefaultRetiredReservations =
        new Dictionary<string, RetiredReservation>
        {
            ["v2026.1101.1-dev"] = new(
                RunId: 41339003060,
                SourceSha: "ce5c63d4da00690f40141435e59db7e8122b972f",
                PackageId: "Piston",
                Reason: "NuGet trusted-publisher login failed before publication."),
        };

    private HttpClient? _http;

    public HttpMessageHandler Handler { get; init; } = new SocketsHttpHandler { AllowAutoRedirect = false };

    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;

    public CommandRunner Run { get; init; } = RunProcessAsync;

    public string? GitHubToken { get; init; }

    public string GitHubApiUrl { get; init; } = "https://api.github.com";

    public string BaseDirectory { get; init; } = Directory.GetCurrentDirectory();

    public IReadOnlyDictionary<string, RetiredReservation> RetiredReservations { get; init; } = DefaultRetiredReservations;

    private HttpClient Http => _http ??= new HttpClient(Handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

    public static ReleaseTooling FromEnvironment(Func<string, string?> env, string baseDirectory) => new()
    {
        GitHubToken = env("GH_TOKEN") is { Length: > 0 } token ? token : null,
        GitHubApiUrl = (env("GITHUB_API_URL") ?? "https://api.github.com").TrimEnd('/'),
        BaseDirectory = baseDirectory,
    };

    public static async Task<string> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, string? workingDirectory)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new ReleaseException($"{fileName} could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ReleaseException($"{fileName} could not be started: {ex.Message}", ex);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var (output, error) = (await stdout, await stderr);
            if (process.ExitCode != 0)
            {
                var detail = error.Trim() is { Length: > 0 } trimmed ? trimmed : output.Trim();
                throw new ReleaseException($"{fileName} failed ({process.ExitCode}): {detail}");
            }

            return output.Trim();
        }
    }

    private Task<string> GitAsync(string sourceDirectory, params string[] arguments) =>
        Run("git", arguments, sourceDirectory);

    private async Task<byte[]?> RequestBytesAsync(string url, string? token = null, bool allowMissing = false)
    {
        var uri = new Uri(url);
        for (var redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("piston-release");
            request.Headers.Accept.ParseAdd(token is null ? "application/octet-stream" : "application/vnd.github+json");
            if (token is not null)
            {
                request.Headers.Authorization = new("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await Http.SendAsync(request);
            }
            catch (HttpRequestException ex)
            {
                throw new ReleaseException($"Request to {url} failed: {ex.Message}", ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new ReleaseException($"Request to {url} timed out.", ex);
            }

            using (response)
            {
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    var target = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (token is not null && Uri.Compare(uri, target, UriComponents.SchemeAndServer,
                            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) != 0)
                    {
                        throw new ReleaseException("Refusing to send GitHub credentials to a different origin.");
                    }

                    if (redirects >= 10)
                    {
                        throw new ReleaseException($"Request to {url} redirected too many times.");
                    }

                    uri = target;
                    continue;
                }

                if (allowMissing && response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new ReleaseException($"Request to {url} failed with HTTP {(int)response.StatusCode}.");
                }

                return await response.Content.ReadAsByteArrayAsync();
            }
        }
    }

    private async Task<JsonElement?> GitHubJsonAsync(string path, bool allowMissing = false)
    {
        var token = GitHubToken ?? throw new ReleaseException("GH_TOKEN is required to inspect release state.");
        if (await RequestBytesAsync($"{GitHubApiUrl.TrimEnd('/')}{path}", token, allowMissing) is not { } data)
        {
            return null;
        }

        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(data);
            value = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ReleaseException($"GitHub returned invalid JSON for {path}.", ex);
        }

        return value.ValueKind == JsonValueKind.Object
            ? value
            : throw new ReleaseException($"GitHub returned an unexpected response for {path}.");
    }

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;

    private static string? String(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? Integer(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out var number)
            ? number
            : null;

    private static List<JsonElement>? Array(JsonElement? element, string name) =>
        Property(element, name) is { ValueKind: JsonValueKind.Array } value ? [.. value.EnumerateArray()] : null;

    private static string? WorkflowPath(string? path) => path?.Split('@', 2)[0];

    // The element's own text before any child, like ElementTree's `text`.
    private static string Text(XElement element) =>
        string.Concat(element.Nodes().TakeWhile(n => n is XText).Cast<XText>().Select(t => t.Value));

    public static string CheckedSha(string? value) =>
        value is not null && ShaPattern().IsMatch(value)
            ? value.ToLowerInvariant()
            : throw new ReleaseException("The source commit must be a full GitHub commit SHA.");

    public static string RepoPath(string repo) =>
        RepoPattern().IsMatch(repo)
            ? $"/repos/{repo}"
            : throw new ReleaseException("GITHUB_REPOSITORY must identify an owner and repository.");

    [GeneratedRegex(@"^[0-9a-fA-F]{40}\z")]
    private static partial Regex ShaPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*\z")]
    private static partial Regex PackageIdPattern();
}

internal sealed partial class ReleaseTooling
{
    public static ReleaseContext SelectContext(
        string eventName, string gitRef, string repo, string workflowSha, JsonElement payload, bool manualPublish)
    {
        _ = RepoPath(repo);
        if (eventName == "workflow_dispatch")
        {
            return gitRef == "refs/heads/main"
                ? new ReleaseContext(CheckedSha(workflowSha), "stable", manualPublish)
                : throw new ReleaseException("Stable releases and validation must be dispatched from main.");
        }

        if (eventName == "workflow_run")
        {
            if (Property(payload, "workflow_run") is not { ValueKind: JsonValueKind.Object } run)
            {
                throw new ReleaseException("The completed CI workflow is missing from the event.");
            }

            if (String(run, "conclusion") != "success"
                || String(run, "event") != "push"
                || String(run, "head_branch") != "main"
                || String(Property(run, "head_repository"), "full_name") != repo)
            {
                throw new ReleaseException("Only successful main-branch push CI runs can publish dev packages.");
            }

            var sourceSha = String(run, "head_sha") ?? throw new ReleaseException("The completed CI workflow has no source commit.");
            return new ReleaseContext(CheckedSha(sourceSha), "dev", true);
        }

        throw new ReleaseException($"Unsupported release event: {eventName}");
    }

    private async Task VerifySourceIdentityAsync(string sourceDirectory, string sourceSha)
    {
        var message = $"Source commit {sourceSha} must declare package identity {PackageId} in its project and MCP metadata.";
        XElement project;
        JsonElement server;
        try
        {
            project = XElement.Parse((await GitAsync(sourceDirectory, "show", $"{sourceSha}:src/Piston/Piston.csproj")).TrimStart('\uFEFF'));
            using var document = JsonDocument.Parse(
                (await GitAsync(sourceDirectory, "show", $"{sourceSha}:src/Piston/Integration/server.json")).TrimStart('\uFEFF'));
            server = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is ReleaseException or XmlException or JsonException)
        {
            throw new ReleaseException(message, ex);
        }

        var declarations = project.Elements()
            .Where(group => group.Name.LocalName == "PropertyGroup")
            .SelectMany(group => group.Elements().Where(e => e.Name.LocalName == "PackageId").Select(e => (Group: group, Element: e)))
            .ToList();
        var packages = Array(server, "packages");
        if (declarations is not [var declaration]
            || declaration.Group.Attribute("Condition") is not null
            || declaration.Element.Attribute("Condition") is not null
            || Text(declaration.Element).Trim() != PackageId
            || packages is not [var package]
            || String(package, "identifier") != PackageId)
        {
            throw new ReleaseException(message);
        }
    }

    public async Task<Reservation> ReadReservationAsync(string sourceDirectory, string tag, ReleaseVersion version)
    {
        var reference = $"refs/tags/{tag}";
        if (await GitAsync(sourceDirectory, "cat-file", "-t", reference) != "tag")
        {
            throw new ReleaseException($"Calendar tag {tag} is not an annotated release reservation.");
        }

        var tagObject = await GitAsync(sourceDirectory, "cat-file", "-p", reference);
        var separator = tagObject.IndexOf("\n\n", StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new ReleaseException($"Calendar tag {tag} has no reservation metadata.");
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in tagObject[..separator].Split('\n'))
        {
            if (line.IndexOf(' ', StringComparison.Ordinal) is var space and >= 0)
            {
                fields[line[..space]] = line[(space + 1)..];
            }
        }

        if (fields.GetValueOrDefault("type") != "commit")
        {
            throw new ReleaseException($"Calendar tag {tag} must point directly to a commit.");
        }

        JsonElement metadata;
        try
        {
            using var document = JsonDocument.Parse(tagObject[(separator + 2)..]);
            metadata = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new ReleaseException($"Calendar tag {tag} has invalid reservation metadata.", ex);
        }

        if (metadata.ValueKind != JsonValueKind.Object
            || Integer(metadata, "piston_release") != 1
            || Integer(metadata, "run_id") is not ({ } runId and >= 1)
            || String(metadata, "version") != version.Version
            || String(metadata, "source_sha") is not { } recordedSha)
        {
            throw new ReleaseException($"Calendar tag {tag} has invalid reservation metadata.");
        }

        var sourceSha = CheckedSha(recordedSha);
        if (fields.GetValueOrDefault("object", "").ToLowerInvariant() != sourceSha)
        {
            throw new ReleaseException($"Calendar tag {tag} does not point to its recorded source commit.");
        }

        var verified = Property(metadata, "verified_through");
        if (verified is { ValueKind: not (JsonValueKind.Null or JsonValueKind.String) })
        {
            throw new ReleaseException($"Calendar tag {tag} has invalid verified-history metadata.");
        }

        return new Reservation(version, runId, sourceSha, verified is not null,
            verified is { ValueKind: JsonValueKind.String } through ? through.GetString() : null);
    }

    private async Task<List<Reservation>> ReservationsAsync(string sourceDirectory, IReadOnlyList<string> tags)
    {
        var records = new List<Reservation>();
        var counters = new Dictionary<(DateOnly, int), string>();
        var runs = new Dictionary<long, string>();
        foreach (var tag in tags)
        {
            if (ReleaseVersion.ParseTag(tag) is not { } version)
            {
                continue;
            }

            if (counters.TryGetValue((version.Day, version.Counter), out var existing))
            {
                throw new ReleaseException($"Calendar counter is reused by {existing} and {tag}.");
            }

            counters[(version.Day, version.Counter)] = tag;
            var record = await ReadReservationAsync(sourceDirectory, tag, version);
            if (runs.TryGetValue(record.RunId, out var reserved))
            {
                throw new ReleaseException($"Workflow run {record.RunId} reserved both {reserved} and {tag}.");
            }

            runs[record.RunId] = tag;
            records.Add(record);
        }

        records.Sort((a, b) => (a.Version.Day, a.Version.Counter).CompareTo((b.Version.Day, b.Version.Counter)));
        for (var index = 0; index < records.Count; index++)
        {
            var previousTag = index > 0 ? records[index - 1].Version.Tag : null;
            if (records[index].HasVerifiedHistory && records[index].VerifiedThrough != previousTag)
            {
                throw new ReleaseException($"Calendar tag {records[index].Version.Tag} has an inconsistent release history.");
            }
        }

        return records;
    }

    private Task<byte[]?> PublicPackageAsync(ReleaseVersion version, bool allowMissing = false, string packageId = PackageId)
    {
        var packageVersion = version.Version.ToLowerInvariant();
        var identifier = packageId.ToLowerInvariant();
        if (!PackageIdPattern().IsMatch(identifier))
        {
            throw new ReleaseException("Invalid NuGet package ID.");
        }

        return RequestBytesAsync(
            $"https://api.nuget.org/v3-flatcontainer/{identifier}/{packageVersion}/{identifier}.{packageVersion}.nupkg",
            allowMissing: allowMissing);
    }

    private static Dictionary<string, byte[]> PackageEntries(byte[] data)
    {
        try
        {
            using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
            var names = archive.Entries.Select(e => e.FullName).ToList();
            if (names.Count != names.Distinct(StringComparer.Ordinal).Count())
            {
                throw new ReleaseException("The package contains duplicate archive entries.");
            }

            var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith('/')))
            {
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                entries[entry.FullName] = buffer.ToArray();
            }

            return entries;
        }
        catch (InvalidDataException ex)
        {
            throw new ReleaseException("The package is not a valid NuGet archive.", ex);
        }
    }

    public static void PackageIdentity(byte[] data, ReleaseVersion version, string sourceSha, string repo)
    {
        var entries = PackageEntries(data);
        var nuspecs = entries.Keys.Where(name => name.EndsWith(".nuspec", StringComparison.Ordinal)).ToList();
        if (nuspecs.Count != 1 || !entries.TryGetValue(".mcp/server.json", out var serverData))
        {
            throw new ReleaseException("The package must contain one nuspec and MCP metadata.");
        }

        var values = new Dictionary<string, XElement>(StringComparer.Ordinal);
        JsonElement server;
        JsonElement package;
        try
        {
            var root = XDocument.Load(new MemoryStream(entries[nuspecs[0]])).Root!;
            foreach (var element in root.Elements().First(e => e.Name.LocalName == "metadata").Elements())
            {
                values[element.Name.LocalName] = element;
            }

            using var document = JsonDocument.Parse(new MemoryStream(serverData));
            server = document.RootElement.Clone();
            if (Property(server, "packages") is null)
            {
                throw new ReleaseException("The package metadata is invalid.");
            }

            package = Array(server, "packages") is [var single]
                ? single
                : throw new ReleaseException("The package must describe exactly one MCP package.");
            if (package.ValueKind != JsonValueKind.Object)
            {
                throw new ReleaseException("The MCP package metadata must be an object.");
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidOperationException or JsonException)
        {
            throw new ReleaseException("The package metadata is invalid.", ex);
        }

        var repository = values.GetValueOrDefault("repository");
        if (values.GetValueOrDefault("id") is not { } id
            || Text(id) != PackageId
            || values.GetValueOrDefault("version") is not { } packageVersion
            || Text(packageVersion) != version.Version
            || repository is null
            || (string?)repository.Attribute("type") != "git"
            || !string.Equals(((string?)repository.Attribute("url") ?? "").TrimEnd('/'), $"https://github.com/{repo}",
                StringComparison.OrdinalIgnoreCase)
            || ((string?)repository.Attribute("commit") ?? "").ToLowerInvariant() != sourceSha
            || String(server, "version") != version.Version
            || String(package, "version") != version.Version
            || String(package, "identifier") != PackageId
            || !entries.Keys.Any(name => name.EndsWith("/Piston.dll", StringComparison.Ordinal)))
        {
            throw new ReleaseException($"Package {version.Version} does not match its tag, repository, or source commit.");
        }
    }

    public static void ComparePackages(byte[] expected, byte[] actual)
    {
        string[] ignored = [".signature.p7s", "[Content_Types].xml"];
        var expectedEntries = PackageEntries(expected).Where(e => !ignored.Contains(e.Key)).ToDictionary();
        var actualEntries = PackageEntries(actual).Where(e => !ignored.Contains(e.Key)).ToDictionary();
        if (expectedEntries.Count != actualEntries.Count
            || expectedEntries.Any(e => !actualEntries.TryGetValue(e.Key, out var data) || !data.AsSpan().SequenceEqual(e.Value)))
        {
            throw new ReleaseException("The existing package does not match this workflow's package.");
        }
    }
}

internal sealed partial class ReleaseTooling
{
    private async Task<JsonElement?> GitHubReleaseAsync(string repo, ReleaseVersion version, bool allowMissing = false)
    {
        var release = await GitHubJsonAsync($"{RepoPath(repo)}/releases/tags/{Uri.EscapeDataString(version.Tag)}", allowMissing);
        if (release is not null
            && (String(release, "tag_name") != version.Tag
                || Property(release, "draft") is not { ValueKind: JsonValueKind.False }
                || Property(release, "prerelease") is not { ValueKind: JsonValueKind.False }))
        {
            throw new ReleaseException($"GitHub release {version.Tag} is not the expected stable release.");
        }

        return release;
    }

    private static List<JsonElement> Assets(JsonElement? release, string name) =>
        (Array(release, "assets") ?? []).Where(asset => String(asset, "name") == name).ToList();

    private async Task<byte[]> DownloadGitHubAssetAsync(string repo, ReleaseVersion version, string fileName)
    {
        var directory = Directory.CreateTempSubdirectory("piston-release-asset-");
        try
        {
            await Run("gh", ["release", "download", version.Tag, "--repo", repo, "--pattern", fileName, "--dir", directory.FullName], null);
            return await File.ReadAllBytesAsync(Path.Combine(directory.FullName, fileName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private async Task VerifyPublicReservationAsync(Reservation record, string repo)
    {
        var data = await PublicPackageAsync(record.Version)
            ?? throw new ReleaseException($"Public package {record.Version.Version} is unavailable.");
        PackageIdentity(data, record.Version, record.SourceSha, repo);
        if (!record.Version.Dev)
        {
            var release = await GitHubReleaseAsync(repo, record.Version)
                ?? throw new ReleaseException($"GitHub release {record.Version.Tag} is unavailable.");
            var expectedName = $"{PackageId}.{record.Version.Version}.nupkg";
            if (Assets(release, expectedName) is not [var asset] || String(asset, "state") != "uploaded")
            {
                throw new ReleaseException($"GitHub release {record.Version.Tag} is missing {expectedName}.");
            }

            ComparePackages(data, await DownloadGitHubAssetAsync(repo, record.Version, expectedName));
        }
    }

    private async Task VerifyRetiredReservationAsync(Reservation record, RetiredReservation retirement, string repo)
    {
        var tag = record.Version.Tag;
        if (record.RunId != retirement.RunId || record.SourceSha != retirement.SourceSha)
        {
            throw new ReleaseException($"Retired reservation {tag} does not match its recorded identity.");
        }

        var run = await GitHubJsonAsync($"{RepoPath(repo)}/actions/runs/{record.RunId}");
        if (run is null
            || Integer(run, "id") != retirement.RunId
            || WorkflowPath(String(run, "path")) != ReleaseWorkflow
            || String(run, "event") != "workflow_run"
            || String(run, "head_sha") != retirement.SourceSha
            || Integer(run, "run_attempt") != 1
            || String(run, "status") != "completed"
            || String(run, "conclusion") != "failure")
        {
            throw new ReleaseException($"Retired reservation {tag} no longer has the expected failed run.");
        }

        var result = await GitHubJsonAsync($"{RepoPath(repo)}/actions/runs/{record.RunId}/attempts/1/jobs?per_page=100");
        var jobs = Array(result, "jobs");
        if (jobs is null || Integer(result, "total_count") != jobs.Count || jobs.Any(j => j.ValueKind != JsonValueKind.Object))
        {
            throw new ReleaseException($"Retired reservation {tag} has incomplete job evidence.");
        }

        if (jobs.Where(j => String(j, "name") == "publish").ToList() is not [var job])
        {
            throw new ReleaseException($"Retired reservation {tag} has ambiguous publish-job evidence.");
        }

        var steps = Array(job, "steps");
        if (String(job, "status") != "completed"
            || String(job, "conclusion") != "failure"
            || steps is null
            || steps.Any(s => s.ValueKind != JsonValueKind.Object))
        {
            throw new ReleaseException($"Retired reservation {tag} has invalid publish-job evidence.");
        }

        if (steps.Where(s => String(s, "name") == "Run NuGet/login@v1").ToList() is not [var login]
            || String(login, "status") != "completed"
            || String(login, "conclusion") != "failure"
            || steps.Where(s => String(s, "name") == "Publish tool to NuGet.org").ToList() is not [var publish]
            || String(publish, "status") != "completed"
            || String(publish, "conclusion") != "skipped")
        {
            throw new ReleaseException($"Retired reservation {tag} does not prove publication was skipped.");
        }

        if (await GitHubJsonAsync($"{RepoPath(repo)}/releases/tags/{Uri.EscapeDataString(tag)}", allowMissing: true) is not null)
        {
            throw new ReleaseException($"Retired reservation {tag} unexpectedly has a GitHub release.");
        }

        foreach (var packageId in new[] { retirement.PackageId, PackageId })
        {
            if (await PublicPackageAsync(record.Version, allowMissing: true, packageId) is not null)
            {
                throw new ReleaseException($"Retired reservation {tag} unexpectedly has a {packageId} package.");
            }
        }

        Console.WriteLine($"Verified retirement of {tag}: {retirement.Reason} Its version remains reserved.");
    }

    private async Task VerifyPreviousAsync(Reservation record, string repo)
    {
        if (RetiredReservations.TryGetValue(record.Version.Tag, out var retirement))
        {
            await VerifyRetiredReservationAsync(record, retirement, repo);
            return;
        }

        var run = await GitHubJsonAsync($"{RepoPath(repo)}/actions/runs/{record.RunId}", allowMissing: true);
        if (run is null)
        {
            var workflow = await GitHubJsonAsync($"{RepoPath(repo)}/actions/workflows/release.yml");
            if (String(workflow, "path") != ReleaseWorkflow)
            {
                throw new ReleaseException("Cannot verify that the release workflow is accessible.");
            }

            Console.WriteLine($"Workflow run {record.RunId} is unavailable; verifying its public release.");
        }
        else if (Integer(run, "id") != record.RunId
                 || WorkflowPath(String(run, "path") ?? "") != ReleaseWorkflow
                 || String(run, "status") != "completed"
                 || String(run, "conclusion") != "success")
        {
            throw new ReleaseException(
                $"Release {record.Version.Tag} from workflow run {record.RunId} is incomplete. "
                + "Resolve and rerun that workflow before publishing another version.");
        }

        await VerifyPublicReservationAsync(record, repo);
    }

    // A checkpoint records that preceding publications or retirements were verified before this tag.
    private async Task VerifyHistoryAsync(IReadOnlyList<Reservation> records, string repo)
    {
        var start = Math.Max(0, records.ToList().FindLastIndex(r => r.HasVerifiedHistory));
        foreach (var record in records.Skip(start))
        {
            await VerifyPreviousAsync(record, repo);
        }
    }

    public async Task<(ReleaseVersion Version, bool Reused)> PrepareAsync(
        string sourceDirectory, string repo, string sourceSha, long runId, bool dev, bool preview)
    {
        sourceSha = CheckedSha(sourceSha);
        if (runId < 1)
        {
            throw new ReleaseException("The workflow run ID must be positive.");
        }

        if (await GitAsync(sourceDirectory, "cat-file", "-t", sourceSha) != "commit")
        {
            throw new ReleaseException($"Source {sourceSha} is not a commit.");
        }

        await VerifySourceIdentityAsync(sourceDirectory, sourceSha);
        await GitAsync(sourceDirectory, "fetch", "--tags", "--no-recurse-submodules", "origin");
        var tags = (await GitAsync(sourceDirectory, "tag", "--list")).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimEnd('\r')).ToList();
        if (preview)
        {
            return (ReleaseVersion.Allocate(Today(), tags, dev), false);
        }

        var records = await ReservationsAsync(sourceDirectory, tags);
        if (records.FirstOrDefault(r => r.RunId == runId) is { } own)
        {
            if (RetiredReservations.ContainsKey(own.Version.Tag))
            {
                throw new ReleaseException($"Release {own.Version.Tag} is retired and cannot be retried.");
            }

            if (own.SourceSha != sourceSha || own.Version.Dev != dev)
            {
                throw new ReleaseException($"Workflow run {runId} already reserved {own.Version.Tag} for different inputs.");
            }

            if (records[^1] != own)
            {
                throw new ReleaseException(
                    $"A later release was reserved after {own.Version.Tag}; refusing an out-of-order retry.");
            }

            if (!own.HasVerifiedHistory)
            {
                await VerifyHistoryAsync(records[..^1], repo);
            }

            return (own.Version, true);
        }

        if (records.Count > 0)
        {
            EnsureClock(records[^1]);
            await VerifyHistoryAsync(records, repo);
        }

        // Verification can cross midnight; allocate against the day the tag is created.
        var today = Today();
        if (records.Count > 0)
        {
            EnsureClock(records[^1]);
        }

        var version = ReleaseVersion.Allocate(today, tags, dev);
        var metadata = new JsonObject
        {
            ["piston_release"] = 1,
            ["run_id"] = runId,
            ["source_sha"] = sourceSha,
            ["version"] = version.Version,
            ["verified_through"] = records.Count > 0 ? records[^1].Version.Tag : null,
        }.ToJsonString();
        await GitAsync(sourceDirectory,
            "-c", "user.name=github-actions[bot]",
            "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
            "tag", "-a", version.Tag, sourceSha, "-m", metadata);
        await GitAsync(sourceDirectory, "push", "origin", $"refs/tags/{version.Tag}");
        return (version, false);

        void EnsureClock(Reservation latest)
        {
            if (Today() < latest.Version.Day)
            {
                throw new ReleaseException($"UTC date precedes the latest release reservation {latest.Version.Tag}.");
            }
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(UtcNow().UtcDateTime);

    public static bool IsReservedRetry(bool reused, string artifact, long runId, long runAttempt)
    {
        if (runId < 1 || runAttempt < 1)
        {
            throw new ReleaseException("The workflow run ID and attempt must be positive.");
        }

        var match = Regex.Match(artifact, $@"^piston-nuget-tool-{runId}-([1-9][0-9]*)\z", RegexOptions.CultureInvariant);
        if (!match.Success
            || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var attempt)
            || attempt > runAttempt)
        {
            throw new ReleaseException("The package artifact does not belong to this workflow run and attempt.");
        }

        // A successful package job may be reused unchanged when only later jobs are retried.
        return reused || attempt < runAttempt;
    }

    public async Task<bool> ReconcileAsync(string packagePath, ReleaseVersion version, string sourceSha, string repo, bool retry)
    {
        var local = await File.ReadAllBytesAsync(packagePath);
        PackageIdentity(local, version, sourceSha, repo);
        if (await PublicPackageAsync(version, allowMissing: true) is not { } existing)
        {
            return true;
        }

        if (!retry)
        {
            throw new ReleaseException($"NuGet already contains {version.Version}; refusing an unrelated duplicate.");
        }

        PackageIdentity(existing, version, sourceSha, repo);
        ComparePackages(local, existing);
        Console.WriteLine($"NuGet already contains the matching {version.Version} package; resuming verification.");
        return false;
    }

    public async Task VerifyPublicPackageAsync(string packagePath, ReleaseVersion version, string sourceSha, string repo)
    {
        var local = await File.ReadAllBytesAsync(packagePath);
        PackageIdentity(local, version, sourceSha, repo);
        var published = await PublicPackageAsync(version)
            ?? throw new ReleaseException($"Public package {version.Version} is unavailable.");
        PackageIdentity(published, version, sourceSha, repo);
        ComparePackages(local, published);
    }

    public async Task EnsureGitHubReleaseAsync(string packagePath, ReleaseVersion version, string sourceSha, string repo)
    {
        if (version.Dev)
        {
            throw new ReleaseException("A dev package must not create a GitHub Release.");
        }

        var local = await File.ReadAllBytesAsync(packagePath);
        PackageIdentity(local, version, sourceSha, repo);
        var release = await GitHubReleaseAsync(repo, version, allowMissing: true);
        if (release is null)
        {
            await Run("gh", ["release", "create", version.Tag, "--repo", repo, "--verify-tag", "--generate-notes"], null);
            release = await GitHubReleaseAsync(repo, version)
                ?? throw new ReleaseException($"GitHub release {version.Tag} is unavailable after creation.");
        }

        var fileName = Path.GetFileName(packagePath);
        switch (Assets(release, fileName))
        {
            case [var asset]:
                if (String(asset, "state") != "uploaded")
                {
                    throw new ReleaseException($"GitHub release asset {fileName} is not fully uploaded.");
                }

                ComparePackages(local, await DownloadGitHubAssetAsync(repo, version, fileName));
                break;
            case []:
                await Run("gh", ["release", "upload", version.Tag, packagePath, "--repo", repo], null);
                break;
            default:
                throw new ReleaseException($"GitHub release {version.Tag} has duplicate {fileName} assets.");
        }
    }
}

internal sealed partial class ReleaseTooling
{
    public static async Task RunCommandAsync(
        string command, Func<string, string?> env, string invocationDirectory, TextWriter output, ReleaseTooling? tooling = null)
    {
        try
        {
            await RunCommandCoreAsync(command, env, invocationDirectory, output, tooling);
        }
        catch (Exception ex) when (ex is ReleaseException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"::error::{ex.Message}");
            throw ex as ReleaseException ?? new ReleaseException(ex.Message, ex);
        }
    }

    private static async Task RunCommandCoreAsync(
        string command, Func<string, string?> env, string invocationDirectory, TextWriter output, ReleaseTooling? tooling)
    {
        string Required(string name) =>
            env(name) is { Length: > 0 } value ? value : throw new ReleaseException($"{name} is required.");

        bool Flag(string name, bool? fallback = null) => (env(name) ?? "") switch
        {
            "true" => true,
            "false" => false,
            "" when fallback is { } value => value,
            _ => throw new ReleaseException($"{name} must be true or false."),
        };

        long Number(string name) =>
            long.TryParse(Required(name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
                ? value
                : throw new ReleaseException($"{name} must be an integer.");

        string FullPath(string name) => Path.GetFullPath(Required(name), invocationDirectory);

        void Write(params (string Name, string Value)[] values)
        {
            var path = env("GITHUB_OUTPUT");
            foreach (var (name, value) in values)
            {
                output.WriteLine($"{name}={value}");
                if (path is { Length: > 0 })
                {
                    File.AppendAllText(path, $"{name}={value}\n");
                }
            }
        }

        static string Bool(bool value) => value ? "true" : "false";

        var repo = Required("GITHUB_REPOSITORY");
        _ = RepoPath(repo);
        tooling ??= FromEnvironment(env, invocationDirectory);
        switch (command)
        {
            case "context":
                {
                    JsonElement payload;
                    try
                    {
                        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(FullPath("GITHUB_EVENT_PATH")));
                        payload = document.RootElement.Clone();
                    }
                    catch (JsonException ex)
                    {
                        throw new ReleaseException("GitHub event payload is invalid JSON.", ex);
                    }

                    if (payload.ValueKind != JsonValueKind.Object)
                    {
                        throw new ReleaseException("GitHub event payload must be an object.");
                    }

                    var selected = SelectContext(Required("GITHUB_EVENT_NAME"), Required("GITHUB_REF"), repo,
                        Required("GITHUB_SHA"), payload, Flag("RELEASE_MANUAL_PUBLISH", fallback: false));
                    Write(("source_sha", selected.SourceSha), ("kind", selected.Kind), ("publish", Bool(selected.Publish)));
                    return;
                }

            case "prepare":
                {
                    var dev = Required("RELEASE_KIND") switch
                    {
                        "dev" => true,
                        "stable" => false,
                        _ => throw new ReleaseException("RELEASE_KIND must be dev or stable."),
                    };
                    var (version, reused) = await tooling.PrepareAsync(FullPath("RELEASE_SOURCE_DIR"), repo,
                        Required("RELEASE_SOURCE_SHA"), Number("GITHUB_RUN_ID"), dev, preview: !Flag("RELEASE_PUBLISH"));
                    Write(("version", version.Version), ("tag", version.Tag), ("reused", Bool(reused)));
                    return;
                }

            case "reconcile" or "verify-package" or "github-release":
                {
                    var packageVersion = Required("RELEASE_VERSION");
                    var version = ReleaseVersion.ParseTag($"v{packageVersion}")
                        ?? throw new ReleaseException($"Invalid calendar package version: {packageVersion}");
                    var sourceSha = CheckedSha(Required("RELEASE_SOURCE_SHA"));
                    var package = FullPath("RELEASE_PACKAGE");
                    if (command == "reconcile")
                    {
                        var retry = IsReservedRetry(Flag("RELEASE_REUSED"), Required("RELEASE_ARTIFACT"),
                            Number("GITHUB_RUN_ID"), Number("GITHUB_RUN_ATTEMPT"));
                        Write(("push", Bool(await tooling.ReconcileAsync(package, version, sourceSha, repo, retry))));
                    }
                    else if (command == "verify-package")
                    {
                        await tooling.VerifyPublicPackageAsync(package, version, sourceSha, repo);
                    }
                    else
                    {
                        await tooling.EnsureGitHubReleaseAsync(package, version, sourceSha, repo);
                    }

                    return;
                }

            default:
                throw new ReleaseException($"Unknown release command: {command}");
        }
    }
}

// ── Release tests (`dotnet run build.cs -- test-release`) ─────────────────────────────────────────────────────────
internal sealed class ReleaseTestFailedException(string message) : Exception(message);

internal static class Expect
{
    public static void Equal<T>(T expected, T actual, string? label = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{label}{(label is null ? "" : ": ")}expected <{expected}> but was <{actual}>.");
        }
    }

    public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? label = null)
    {
        var (e, a) = (expected.ToList(), actual.ToList());
        if (!e.SequenceEqual(a))
        {
            throw new InvalidOperationException($"{label}{(label is null ? "" : ": ")}expected [{string.Join(", ", e)}] but was [{string.Join(", ", a)}].");
        }
    }

    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Contains(string expected, string actual) =>
        True(actual.Contains(expected, StringComparison.Ordinal), $"expected <{actual}> to contain <{expected}>.");

    public static ReleaseException Throws(Action action, string? contains = null, string? label = null) =>
        ThrowsAsync(() => { action(); return Task.CompletedTask; }, contains, label).GetAwaiter().GetResult();

    public static async Task<ReleaseException> ThrowsAsync(Func<Task> action, string? contains = null, string? label = null)
    {
        try
        {
            await action();
        }
        catch (ReleaseException ex)
        {
            if (contains is not null)
            {
                True(ex.Message.Contains(contains, StringComparison.Ordinal),
                    $"{label}{(label is null ? "" : ": ")}expected error containing <{contains}> but was <{ex.Message}>.");
            }

            return ex;
        }

        throw new InvalidOperationException($"{label}{(label is null ? "" : ": ")}expected a ReleaseException.");
    }
}

internal static partial class ReleaseTests
{
    private static readonly DateOnly Day = new(2026, 11, 1);

    private static readonly List<(string Name, Func<Task> Body)> Cases = [];

    private static void Case(string name, Action body) => Cases.Add((name, () => { body(); return Task.CompletedTask; }));

    private static void Case(string name, Func<Task> body) => Cases.Add((name, body));

    public static async Task RunAsync()
    {
        RegisterVersionTests();
        RegisterWorkflowTests();
        RegisterContextTests();
        RegisterReservationTests();
        RegisterReservationHistoryTests();
        RegisterRetiredReservationTests();
        RegisterPackageTests();
        var failures = 0;
        foreach (var (name, body) in Cases)
        {
            try
            {
                await body();
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"FAIL {name}: {ex}");
            }
        }

        Console.WriteLine($"Release tests: {Cases.Count - failures} passed, {failures} failed.");
        if (failures > 0)
        {
            throw new ReleaseTestFailedException($"{failures} release test(s) failed.");
        }
    }

    private static void RegisterVersionTests()
    {
        Case("version formats canonical tags", () =>
        {
            foreach (var (day, counter, dev, version, tag) in new[]
                     {
                         (new DateOnly(2026, 1, 1), 1, true, "2026.101.1-dev", "v2026.101.1-dev"),
                         (new DateOnly(2024, 2, 29), 42, false, "2024.229.42", "v2024.229.42"),
                         (new DateOnly(2026, 11, 1), 2, false, "2026.1101.2", "v2026.1101.2"),
                         (new DateOnly(2026, 12, 31), 9, true, "2026.1231.9-dev", "v2026.1231.9-dev"),
                         (new DateOnly(2027, 1, 1), 1, false, "2027.101.1", "v2027.101.1"),
                     })
            {
                var release = new ReleaseVersion(day, counter, dev);
                Expect.Equal(version, release.Version);
                Expect.Equal(tag, release.Tag);
            }
        });

        Case("version rejects out-of-range counters", () =>
        {
            foreach (var counter in new[] { -1, 0, 65535 })
            {
                Expect.Throws(() => _ = new ReleaseVersion(Day, counter, false), label: counter.ToString(CultureInfo.InvariantCulture));
            }

            Expect.Equal("2026.1101.1", new ReleaseVersion(Day, 1, false).Version);
            Expect.Equal("2026.1101.65534", new ReleaseVersion(Day, 65534, false).Version);
        });

        Case("generated versions sort by numeric calendar components", () =>
        {
            var versions = new[]
            {
                (new DateOnly(2027, 1, 1), 1), (new DateOnly(2026, 11, 1), 1), (new DateOnly(2024, 3, 1), 1),
                (new DateOnly(2026, 1, 1), 1), (new DateOnly(2026, 12, 31), 1), (new DateOnly(2025, 12, 31), 65534),
                (new DateOnly(2026, 10, 31), 1), (new DateOnly(2024, 2, 29), 5), (new DateOnly(2026, 2, 1), 1),
                (new DateOnly(2026, 3, 1), 1),
            }.Select(v => new ReleaseVersion(v.Item1, v.Item2, false).Version);
            var ordered = versions.OrderBy(v => v, Comparer<string>.Create((a, b) =>
                a.Split('.').Select(int.Parse).Zip(b.Split('.').Select(int.Parse), (x, y) => x.CompareTo(y))
                    .FirstOrDefault(c => c != 0)));
            Expect.Sequence(
                ["2024.229.5", "2024.301.1", "2025.1231.65534", "2026.101.1", "2026.201.1", "2026.301.1", "2026.1031.1",
                 "2026.1101.1", "2026.1231.1", "2027.101.1"],
                ordered);
        });

        Case("parse canonical tags returns release version", () =>
        {
            foreach (var (tag, day, counter, dev, version) in new[]
                     {
                         ("v2026.101.1", new DateOnly(2026, 1, 1), 1, false, "2026.101.1"),
                         ("v2024.229.42-dev", new DateOnly(2024, 2, 29), 42, true, "2024.229.42-dev"),
                         ("v2026.1101.2", new DateOnly(2026, 11, 1), 2, false, "2026.1101.2"),
                         ("v2026.1231.65534-dev", new DateOnly(2026, 12, 31), 65534, true, "2026.1231.65534-dev"),
                         ("v2027.101.1", new DateOnly(2027, 1, 1), 1, false, "2027.101.1"),
                     })
            {
                var release = ReleaseVersion.ParseTag(tag) ?? throw new InvalidOperationException($"{tag} did not parse.");
                Expect.Equal(day, release.Day, tag);
                Expect.Equal(counter, release.Counter, tag);
                Expect.Equal(dev, release.Dev, tag);
                Expect.Equal(version, release.Version, tag);
                Expect.Equal(tag, release.Tag, tag);
            }
        });

        Case("parse ignores unrelated legacy tags", () =>
        {
            foreach (var tag in new[] { "v0.1.0", "v1.2.3", "v2.0.0-rc.1", "v999.12.1", "release-2026.101.1", "nightly", "" })
            {
                Expect.Equal(null, ReleaseVersion.ParseTag(tag), tag);
            }
        });

        Case("parse rejects malformed calendar tags", () =>
        {
            foreach (var tag in new[]
                     {
                         "v2026.229.1", "v2024.230.1", "v2026.431.1", "v2026.132.1", "v2026.1100.1", "v2026.001.1",
                         "v2026.1301.1", "v02026.101.1", "v2026.0101.1", "v2026.101.01", "v2026.101.0", "v2026.101.-1",
                         "v2026.101.65535", "v2026.101", "v2026..1", "v2026.101.1-DEV", "v2026.101.1-rc.1",
                         "v2026.101.1-dev+deadbeef", "v2026.101.1\n", "v2026.101.1\0", "v2026.101.99999999999",
                     })
            {
                Expect.Throws(() => ReleaseVersion.ParseTag(tag), label: tag);
            }
        });

        Case("allocate starts at one without same-day tags", () =>
        {
            foreach (var (tags, dev, version) in new[]
                     {
                         (System.Array.Empty<string>(), false, "2026.1101.1"),
                         (["v0.1.0", "nightly"], true, "2026.1101.1-dev"),
                         (["v2026.1031.91", "v2026.1102.8-dev"], false, "2026.1101.1"),
                     })
            {
                Expect.Equal(version, ReleaseVersion.Allocate(Day, tags, dev).Version);
            }
        });

        Case("allocate shares counter across stable and dev", () =>
        {
            Expect.Equal("2026.1101.4",
                ReleaseVersion.Allocate(Day, ["v2026.1101.1-dev", "v2026.1101.2", "v2026.1101.3-dev"], false).Version);
            Expect.Equal("2026.1101.9-dev", ReleaseVersion.Allocate(Day, ["v2026.1101.8", "v2026.1101.6-dev"], true).Version);
        });

        Case("allocate uses numeric max, not lexical order", () =>
        {
            var release = ReleaseVersion.Allocate(Day, ["v2026.1101.9", "v2026.1101.10-dev", "v2026.1101.2", "v2026.1031.60000"], false);
            Expect.Equal("2026.1101.11", release.Version);
            Expect.Equal("v2026.1101.11", release.Tag);
        });

        Case("allocate increments adjacent counter boundaries", () =>
        {
            foreach (var (previous, version) in new[]
                     {
                         (1, "2026.1101.2-dev"), (9, "2026.1101.10-dev"), (10, "2026.1101.11-dev"),
                         (65532, "2026.1101.65533-dev"), (65533, "2026.1101.65534-dev"),
                     })
            {
                Expect.Equal(version, ReleaseVersion.Allocate(Day, [$"v2026.1101.{previous}"], true).Version);
            }

            Expect.Throws(() => ReleaseVersion.Allocate(Day, ["v2026.1101.65534-dev"], false));
        });

        Case("allocate resets across day and year", () =>
        {
            Expect.Equal("2026.1102.1", ReleaseVersion.Allocate(new(2026, 11, 2), ["v2026.1101.65534-dev"], false).Version);
            Expect.Equal("2024.229.1", ReleaseVersion.Allocate(new(2024, 2, 29), ["v2024.228.65534"], false).Version);
            Expect.Equal("2027.101.1", ReleaseVersion.Allocate(new(2027, 1, 1), ["v2026.1231.65534-dev"], false).Version);
        });

        Case("allocate rejects reused counter for stable and dev", () =>
        {
            Expect.Throws(() => ReleaseVersion.Allocate(Day, ["v2026.1101.7", "v2026.1101.7-dev"], true));
            Expect.Throws(() => ReleaseVersion.Allocate(Day, ["v2026.1101.7-dev", "v2026.1101.7"], true));
        });

        Case("allocate rejects invalid calendar tags", () =>
        {
            foreach (var tag in new[] { "v2026.229.4", "v02026.1101.8", "v2026.1101.01", "v2026.1101.65535" })
            {
                Expect.Throws(() => ReleaseVersion.Allocate(Day, ["v2026.1101.1", tag], true), label: tag);
            }
        });

        Case("allocate accepts a single-pass iterable", () =>
        {
            static IEnumerable<string> Once()
            {
                yield return "v2026.1101.2-dev";
                yield return "v2026.1101.4";
            }

            Expect.Equal("2026.1101.5-dev", ReleaseVersion.Allocate(Day, Once(), true).Version);
        });
    }
}

// An explicit route table; unknown HTTP requests never reach the network.
internal sealed class FakeHttp : HttpMessageHandler
{
    public Dictionary<string, object> Responses { get; } = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public void Json(string url, JsonNode value) => Responses[url] = Encoding.UTF8.GetBytes(value.ToJsonString());

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.AbsoluteUri;
        Requests.Add(url);
        if (!Responses.TryGetValue(url, out var response))
        {
            throw new InvalidOperationException($"Unexpected HTTP request: {url}");
        }

        if (response is Func<object> factory)
        {
            response = factory();
        }

        return response switch
        {
            int status => Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status)),
            Exception ex => Task.FromException<HttpResponseMessage>(ex),
            byte[] data => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(data) }),
            _ => throw new InvalidOperationException($"Unsupported fake response for {url}."),
        };
    }
}

internal class ReleaseFixture : IDisposable
{
    public const string Repo = "example/piston";
    public const string Api = "https://github.invalid";
    public const string WorkflowUrl = $"{Api}/repos/{Repo}/actions/workflows/release.yml";

    public ReleaseFixture()
    {
        Root = Directory.CreateTempSubdirectory("piston-release-tests-").FullName;
        Http.Json(WorkflowUrl, new JsonObject { ["path"] = ".github/workflows/release.yml" });
    }

    public string Root { get; }

    public FakeHttp Http { get; } = new();

    public DateTimeOffset Clock { get; set; } = new(2026, 11, 1, 23, 59, 0, TimeSpan.Zero);

    public Dictionary<string, RetiredReservation> Retired { get; set; } = new(ReleaseTooling.DefaultRetiredReservations);

    // Returns null to run the real command.
    public Func<string, IReadOnlyList<string>, string?, Task<string?>>? Intercept { get; set; }

    public ReleaseTooling Tooling() => new()
    {
        Handler = Http,
        UtcNow = () => Clock,
        GitHubToken = "fixture-token",
        GitHubApiUrl = Api,
        BaseDirectory = Root,
        RetiredReservations = Retired,
        Run = async (file, arguments, cwd) =>
            (Intercept is { } intercept ? await intercept(file, arguments, cwd) : null)
            ?? await ReleaseTooling.RunProcessAsync(file, arguments, cwd),
    };

    public static string ActionUrl(long runId) => $"{Api}/repos/{Repo}/actions/runs/{runId}";

    public static string AttemptJobsUrl(long runId) => $"{Api}/repos/{Repo}/actions/runs/{runId}/attempts/1/jobs?per_page=100";

    public static string ReleaseUrl(ReleaseVersion version) => $"{Api}/repos/{Repo}/releases/tags/{version.Tag}";

    public static string PackageUrl(ReleaseVersion version, string packageId = ReleaseTooling.PackageId)
    {
        var (number, id) = (version.Version.ToLowerInvariant(), packageId.ToLowerInvariant());
        return $"https://api.nuget.org/v3-flatcontainer/{id}/{number}/{id}.{number}.nupkg";
    }

    public static JsonObject With(JsonObject value, params (string Name, JsonNode? Value)[] changes)
    {
        var copy = (JsonObject)value.DeepClone();
        foreach (var (name, change) in changes)
        {
            copy[name] = change?.DeepClone();
        }

        return copy;
    }

    public static byte[] MakePackage(
        ReleaseVersion version,
        string sourceSha,
        string repo = Repo,
        string? nuspecVersion = null,
        string? mcpVersion = null,
        string packageId = ReleaseTooling.PackageId,
        string identifier = ReleaseTooling.PackageId,
        string dll = "expected executable",
        string signature = "signature")
    {
        var number = version.Version;
        var nuspec = "<package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata>"
            + $"<id>{packageId}</id><version>{nuspecVersion ?? number}</version>"
            + $"<repository type=\"git\" url=\"https://github.com/{repo}\" commit=\"{sourceSha}\" />"
            + "</metadata></package>";
        var server = new JsonObject
        {
            ["version"] = mcpVersion ?? number,
            ["packages"] = new JsonArray(new JsonObject { ["identifier"] = identifier, ["version"] = mcpVersion ?? number }),
        };
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in new[]
                     {
                         ($"{packageId}.nuspec", nuspec), (".mcp/server.json", server.ToJsonString()),
                         ("tools/net10.0/any/Piston.dll", dll), (".signature.p7s", signature),
                     })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }

        return buffer.ToArray();
    }

    public void Success(long runId) => Http.Json(ActionUrl(runId), new JsonObject
    {
        ["id"] = runId,
        ["path"] = ".github/workflows/release.yml@refs/heads/main",
        ["status"] = "completed",
        ["conclusion"] = "success",
    });

    public void PublicPackage(ReleaseVersion version, string sourceSha, string dll = "expected executable", string signature = "signature") =>
        Http.Responses[PackageUrl(version)] = MakePackage(version, sourceSha, dll: dll, signature: signature);

    public void PublicRelease(ReleaseVersion version, JsonArray? assets = null, params (string Name, JsonNode? Value)[] changes) =>
        Http.Json(ReleaseUrl(version), With(new JsonObject
        {
            ["tag_name"] = version.Tag,
            ["draft"] = false,
            ["prerelease"] = false,
            ["assets"] = assets ?? new JsonArray(new JsonObject
            {
                ["name"] = $"DamianH.Piston.{version.Version}.nupkg",
                ["state"] = "uploaded",
            }),
        }, changes));

    public string LocalPackage(ReleaseVersion version, string sourceSha)
    {
        var path = Path.Combine(Root, $"DamianH.Piston.{version.Version}.nupkg");
        File.WriteAllBytes(path, MakePackage(version, sourceSha));
        return path;
    }

    // Serves `gh release download` from the given bytes, like a patched asset download.
    public void DownloadAsset(byte[] data) => Intercept = (file, arguments, _) =>
    {
        if (file == "gh" && arguments is ["release", "download", ..])
        {
            var name = arguments[arguments.ToList().IndexOf("--pattern") + 1];
            File.WriteAllBytes(Path.Combine(arguments[arguments.ToList().IndexOf("--dir") + 1], name), data);
            return Task.FromResult<string?>("");
        }

        return Task.FromResult<string?>(null);
    };

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class GitFixture : ReleaseFixture
{
    private GitFixture()
    {
        Retired = [];
        Origin = Path.Combine(Root, "origin.git");
        Worktree = Directory.CreateDirectory(Path.Combine(Root, "worktree")).FullName;
    }

    public string Origin { get; }

    public string Worktree { get; }

    public string SourceSha { get; private set; } = "";

    public static async Task<GitFixture> CreateAsync()
    {
        var fixture = new GitFixture();
        await ReleaseTooling.RunProcessAsync("git", ["init", "--quiet", "--bare", "--initial-branch=main", fixture.Origin], null);
        await fixture.Git("init", "--quiet", "--initial-branch=main");
        await fixture.WriteSourceIdentity();
        fixture.SourceSha = await fixture.Commit("First tested commit");
        await fixture.Git("remote", "add", "origin", fixture.Origin);
        await fixture.Git("push", "--quiet", "--set-upstream", "origin", "main");
        return fixture;
    }

    public Task<string> Git(params string[] arguments) => ReleaseTooling.RunProcessAsync("git", arguments, Worktree);

    public string ProjectPath => Path.Combine(Worktree, "src/Piston/Piston.csproj");

    public string ServerPath => Path.Combine(Worktree, "src/Piston/Integration/server.json");

    public async Task WriteSourceIdentity(string packageId = ReleaseTooling.PackageId, string identifier = ReleaseTooling.PackageId)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ServerPath)!);
        await File.WriteAllTextAsync(ProjectPath, $"<Project><PropertyGroup><PackageId>{packageId}</PackageId></PropertyGroup></Project>");
        await File.WriteAllTextAsync(ServerPath, new JsonObject
        {
            ["packages"] = new JsonArray(new JsonObject { ["identifier"] = identifier }),
        }.ToJsonString());
        await Git("add", ProjectPath, ServerPath);
    }

    public async Task<string> Commit(string message)
    {
        await Git("-c", "user.name=Release Test", "-c", "user.email=release@example.invalid", "-c", "commit.gpgsign=false",
            "commit", "--quiet", "--allow-empty", "-m", message);
        return await Git("rev-parse", "HEAD");
    }

    public async Task<List<string>> OriginTags() =>
        [.. (await Git("--git-dir", Origin, "tag", "--list")).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    public async Task Tag(ReleaseVersion version, long runId, string? sourceSha = null, string? body = null, bool annotated = true)
    {
        sourceSha ??= SourceSha;
        body ??= new JsonObject
        {
            ["piston_release"] = 1,
            ["run_id"] = runId,
            ["source_sha"] = sourceSha,
            ["version"] = version.Version,
        }.ToJsonString();
        if (annotated)
        {
            await Git("-c", "user.name=Release Test", "-c", "user.email=release@example.invalid", "-c", "tag.gpgsign=false",
                "tag", "-a", version.Tag, sourceSha, "-m", body);
        }
        else
        {
            await Git("tag", version.Tag, sourceSha);
        }

        await Git("push", "--quiet", "origin", $"refs/tags/{version.Tag}");
    }

    public static string TagBody(string raw) => raw[(raw.IndexOf("\n\n", StringComparison.Ordinal) + 2)..];

    public Task<(ReleaseVersion Version, bool Reused)> PrepareAt(
        DateOnly day, string? sourceSha = null, long runId = 100, bool dev = true, bool preview = false)
    {
        Clock = new DateTimeOffset(day.Year, day.Month, day.Day, 23, 59, 0, TimeSpan.Zero);
        return Tooling().PrepareAsync(Worktree, Repo, sourceSha ?? SourceSha, runId, dev, preview);
    }
}

internal static partial class ReleaseTests
{
    private const string Repo = ReleaseFixture.Repo;

    private static JsonElement Element(JsonNode node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.Clone();
    }

    private static bool CanRun(string job, Dictionary<string, string> results, bool publish = true, bool cancelled = false)
    {
        var workflow = File.ReadAllText(ReleaseTooling.ReleaseWorkflow).ReplaceLineEndings("\n");
        var jobMatch = Regex.Match(workflow, $@"^  {Regex.Escape(job)}:\n(.*?)(?=^  [a-z][a-z0-9-]*:\n|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);
        Expect.True(jobMatch.Success, $"Missing job: {job}");
        var body = jobMatch.Groups[1].Value;
        var needs = Regex.Match(body, @"^    needs: \[([^\]]+)\][ \t]*$", RegexOptions.Multiline);
        Expect.True(needs.Success, $"Missing direct dependencies: {job}");
        var direct = needs.Groups[1].Value.Split(',').Select(n => n.Trim()).ToHashSet();
        var guardMatch = Regex.Match(body, @"^    if: >-\n((?:^      .+\n)+)", RegexOptions.Multiline);
        Expect.True(guardMatch.Success, $"Missing explicit job guard: {job}");
        var guard = string.Join(" ", guardMatch.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

        var checks = new List<bool>();
        var resultChecks = new HashSet<string>();
        var (hasCancellation, hasPublish) = (false, false);
        foreach (var clause in guard.Split(" && "))
        {
            if (clause == "!cancelled()")
            {
                hasCancellation = true;
                checks.Add(!cancelled);
            }
            else if (clause == "needs.context.outputs.publish == 'true'")
            {
                hasPublish = true;
                checks.Add(publish);
            }
            else
            {
                var match = Regex.Match(clause, @"^needs\.([a-z-]+)\.result == 'success'\z");
                Expect.True(match.Success, $"Unsupported job guard clause: {clause}");
                resultChecks.Add(match.Groups[1].Value);
                checks.Add(results[match.Groups[1].Value] == "success");
            }
        }

        // A status function removes the implicit success() gate on skipped ancestors.
        Expect.True(hasCancellation, $"Missing cancellation guard: {job}");
        Expect.True(direct.SetEquals(resultChecks), $"{job} guards [{string.Join(", ", resultChecks)}] but needs [{string.Join(", ", direct)}].");
        Expect.Equal(job == "publish", hasPublish, $"{job} publish guard");
        return checks.All(c => c);
    }

    private static void RegisterWorkflowTests()
    {
        Case("skipped stable checks do not block dev publish or verification", () =>
        {
            var results = new Dictionary<string, string>
            {
                ["context"] = "success",
                ["test"] = "skipped",
                ["distribution"] = "skipped",
                ["package"] = "success",
                ["publish"] = "success",
            };
            Expect.True(CanRun("publish", results), "publish should run");
            Expect.True(CanRun("verify-public", results), "verify-public should run");
        });

        Case("stable success allows publish and verification", () =>
        {
            var results = new Dictionary<string, string>
            {
                ["context"] = "success",
                ["test"] = "success",
                ["distribution"] = "success",
                ["package"] = "success",
                ["publish"] = "success",
            };
            Expect.True(CanRun("publish", results), "publish should run");
            Expect.True(CanRun("verify-public", results), "verify-public should run");
        });

        Case("failed, cancelled or skipped direct dependencies block jobs", () =>
        {
            foreach (var (job, dependencies) in new[] { ("publish", new[] { "context", "package" }), ("verify-public", ["context", "package", "publish"]) })
            {
                foreach (var dependency in dependencies)
                {
                    foreach (var result in new[] { "failure", "cancelled", "skipped" })
                    {
                        var results = new Dictionary<string, string> { ["context"] = "success", ["package"] = "success", ["publish"] = "success" };
                        results[dependency] = result;
                        Expect.True(!CanRun(job, results), $"{job} should not run when {dependency} is {result}");
                    }
                }
            }
        });

        Case("workflow cancellation blocks publish and verification", () =>
        {
            var results = new Dictionary<string, string> { ["context"] = "success", ["package"] = "success", ["publish"] = "success" };
            Expect.True(!CanRun("publish", results, cancelled: true), "publish should not run");
            Expect.True(!CanRun("verify-public", results, cancelled: true), "verify-public should not run");
        });

        Case("validation only skips publish and verification", () =>
        {
            var results = new Dictionary<string, string> { ["context"] = "success", ["package"] = "success", ["publish"] = "skipped" };
            Expect.True(!CanRun("publish", results, publish: false), "publish should not run");
            Expect.True(!CanRun("verify-public", results, publish: false), "verify-public should not run");
        });
    }

    private static void RegisterContextTests()
    {
        var testedSha = new string('a', 40);
        var workflowSha = new string('b', 40);
        JsonObject Event(params (string Name, JsonNode? Value)[] changes) => new()
        {
            ["workflow_run"] = ReleaseFixture.With(new JsonObject
            {
                ["conclusion"] = "success",
                ["event"] = "push",
                ["head_branch"] = "main",
                ["head_repository"] = new JsonObject { ["full_name"] = Repo },
                ["head_sha"] = testedSha,
            }, changes),
        };
        var empty = Element(new JsonObject());

        Case("context uses the successful main push CI SHA even when stale", () =>
        {
            var selected = ReleaseTooling.SelectContext("workflow_run", "refs/heads/main", Repo, workflowSha, Element(Event()), false);
            Expect.Equal(new ReleaseContext(testedSha, "dev", true), selected);
        });

        Case("context rejects nonqualifying CI events", () =>
        {
            foreach (var (label, change) in new (string, (string, JsonNode?))[]
                     {
                         ("failed", ("conclusion", "failure")), ("cancelled", ("conclusion", "cancelled")),
                         ("incomplete", ("conclusion", null)), ("pull_request", ("event", "pull_request")),
                         ("fork", ("head_repository", new JsonObject { ["full_name"] = "fork/piston" })),
                         ("missing_repository", ("head_repository", null)), ("non_main_branch", ("head_branch", "feature")),
                     })
            {
                Expect.Throws(() => ReleaseTooling.SelectContext("workflow_run", "refs/heads/main", Repo, workflowSha, Element(Event(change)), false),
                    label: label);
            }
        });

        Case("context on manual main selects stable publish or validation", () =>
        {
            foreach (var publish in new[] { true, false })
            {
                var selected = ReleaseTooling.SelectContext("workflow_dispatch", "refs/heads/main", Repo, workflowSha.ToUpperInvariant(), empty, publish);
                Expect.Equal(new ReleaseContext(workflowSha, "stable", publish), selected);
            }
        });

        Case("context rejects manual non-main", () =>
        {
            foreach (var gitRef in new[] { "refs/heads/feature", "refs/tags/v2026.1101.1" })
            {
                Expect.Throws(() => ReleaseTooling.SelectContext("workflow_dispatch", gitRef, Repo, workflowSha, empty, true), label: gitRef);
            }
        });

        Case("context rejects invalid source SHA", () =>
        {
            foreach (var sha in new JsonNode?[] { "abc123", new string('g', 40), null })
            {
                Expect.Throws(() => ReleaseTooling.SelectContext("workflow_run", "refs/heads/main", Repo, workflowSha, Element(Event(("head_sha", sha))), false));
            }

            Expect.Throws(() => ReleaseTooling.SelectContext("workflow_dispatch", "refs/heads/main", Repo, "abc123", empty, false));
        });

        Case("context rejects missing or unsupported events", () =>
        {
            Expect.Throws(() => ReleaseTooling.SelectContext("workflow_run", "refs/heads/main", Repo, workflowSha, empty, false));
            Expect.Throws(() => ReleaseTooling.SelectContext("workflow_run", "refs/heads/main", Repo, workflowSha,
                Element(new JsonObject { ["workflow_run"] = new JsonArray() }), false));
            Expect.Throws(() => ReleaseTooling.SelectContext("push", "refs/heads/main", Repo, workflowSha, Element(Event()), false));
        });

        Case("context command exports the tested SHA and dev publication", async () =>
        {
            using var fixture = new ReleaseFixture();
            var eventPath = Path.Combine(fixture.Root, "event.json");
            var outputPath = Path.Combine(fixture.Root, "output");
            await File.WriteAllTextAsync(eventPath, Event().ToJsonString());
            var env = new Dictionary<string, string>
            {
                ["GITHUB_EVENT_NAME"] = "workflow_run",
                ["GITHUB_REF"] = "refs/heads/main",
                ["GITHUB_SHA"] = workflowSha,
                ["GITHUB_EVENT_PATH"] = "event.json",
                ["GITHUB_REPOSITORY"] = Repo,
                ["RELEASE_MANUAL_PUBLISH"] = "false",
                ["GITHUB_OUTPUT"] = outputPath,
            };
            var stdout = new StringWriter();
            await ReleaseTooling.RunCommandAsync("context", env.GetValueOrDefault, fixture.Root, stdout, fixture.Tooling());
            var expected = $"source_sha={testedSha}\nkind=dev\npublish=true\n";
            Expect.Equal(expected, await File.ReadAllTextAsync(outputPath));
            Expect.Equal(expected, stdout.ToString().ReplaceLineEndings("\n"));
        });
    }
}

internal static partial class ReleaseTests
{
    private static void GitCase(string name, Func<GitFixture, Task> body) => Case(name, async () =>
    {
        using var fixture = await GitFixture.CreateAsync();
        await body(fixture);
    });

    private static JsonObject Run(long id, string status, string? conclusion, string path = ".github/workflows/release.yml") => new()
    {
        ["id"] = id,
        ["path"] = path,
        ["status"] = status,
        ["conclusion"] = conclusion,
    };

    private static async Task ExpectTags(GitFixture f, params string[] tags) => Expect.Sequence(tags, await f.OriginTags(), "origin tags");

    private static void RegisterReservationTests()
    {
        GitCase("prepare pushes annotated reservation to local origin", async f =>
        {
            var (version, reused) = await f.PrepareAt(Day, runId: 41);
            Expect.Equal("2026.1101.1-dev", version.Version);
            Expect.Equal("v2026.1101.1-dev", version.Tag);
            Expect.Equal(false, reused);
            await ExpectTags(f, "v2026.1101.1-dev");
            Expect.Equal("tag", await f.Git("--git-dir", f.Origin, "cat-file", "-t", $"refs/tags/{version.Tag}"));
            var raw = await f.Git("--git-dir", f.Origin, "cat-file", "-p", $"refs/tags/{version.Tag}");
            var headers = raw[..raw.IndexOf("\n\n", StringComparison.Ordinal)].Split('\n');
            Expect.True(headers.Contains($"object {f.SourceSha}"), "tag object header");
            Expect.True(headers.Contains("type commit"), "tag type header");
            Expect.Equal(
                $$"""{"piston_release":1,"run_id":41,"source_sha":"{{f.SourceSha}}","version":"2026.1101.1-dev","verified_through":null}""",
                GitFixture.TagBody(raw));
        });

        GitCase("prepare retry reuses reservation after UTC rollover", async f =>
        {
            var (first, reused) = await f.PrepareAt(Day, runId: 41);
            Expect.Equal(false, reused);
            var original = await f.Git("rev-parse", $"refs/tags/{first.Tag}");
            (var second, reused) = await f.PrepareAt(new(2026, 11, 2), runId: 41);
            Expect.Equal(true, reused);
            Expect.Equal("2026.1101.1-dev", second.Version);
            Expect.Equal(original, await f.Git("rev-parse", $"refs/tags/{second.Tag}"));
            await ExpectTags(f, "v2026.1101.1-dev");
        });

        GitCase("prepare uses UTC date after prior release verification", async f =>
        {
            var prior = new ReleaseVersion(Day, 1, true);
            await f.Tag(prior, 41);
            f.Success(41);
            f.Http.Responses[ReleaseFixture.PackageUrl(prior)] = (Func<object>)(() =>
            {
                f.Clock = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
                return ReleaseFixture.MakePackage(prior, f.SourceSha);
            });
            f.Clock = new DateTimeOffset(2026, 11, 1, 23, 59, 0, TimeSpan.Zero);
            var (version, reused) = await f.Tooling().PrepareAsync(f.Worktree, Repo, f.SourceSha, 42, dev: false, preview: false);
            Expect.Equal(false, reused);
            Expect.Equal("2026.1102.1", version.Version);
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1102.1");
        });

        GitCase("prepare reserves earlier commit after main advances", async f =>
        {
            var tested = f.SourceSha;
            var latest = await f.Commit("Newer main commit");
            await f.Git("push", "--quiet", "origin", "main");
            var (version, reused) = await f.PrepareAt(Day, sourceSha: tested, runId: 42);
            Expect.Equal(false, reused);
            Expect.Equal(latest, await f.Git("--git-dir", f.Origin, "rev-parse", "refs/heads/main"));
            Expect.Equal(tested, await f.Git("--git-dir", f.Origin, "rev-parse", $"{version.Tag}^{{}}"));
        });

        GitCase("prepare rejects retry with changed inputs", async f =>
        {
            await f.PrepareAt(Day, runId: 41);
            var newSha = await f.Commit("Different source");
            await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), sourceSha: newSha, runId: 41), label: "changed source");
            await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), runId: 41, dev: false), label: "changed kind");
            await ExpectTags(f, "v2026.1101.1-dev");
        });

        GitCase("prepare rejects out-of-order retry", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 41);
            await f.Tag(new ReleaseVersion(Day, 2, false), 42);
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 41));
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2");
        });

        GitCase("prepare preview does not create a tag", async f =>
        {
            var (version, reused) = await f.PrepareAt(Day, runId: 41, dev: false, preview: true);
            Expect.Equal("2026.1101.1", version.Version);
            Expect.Equal(false, reused);
            await ExpectTags(f);
        });

        GitCase("prepare rejects stale or inconsistent source before reserving", async f =>
        {
            foreach (var (label, packageId, identifier) in new[]
                     {
                         ("old_package", "Piston", "DamianH.Piston"), ("old_mcp_identifier", "DamianH.Piston", "Piston"), ("both_old", "Piston", "Piston"),
                     })
            {
                await f.WriteSourceIdentity(packageId, identifier);
                var sha = await f.Commit(label);
                await Expect.ThrowsAsync(() => f.PrepareAt(Day, sourceSha: sha, runId: 41), "package identity", label);
                await ExpectTags(f);
                Expect.Equal("", await f.Git("tag", "--list"), label);
                Expect.Equal(0, f.Http.Requests.Count, label);
            }
        });

        GitCase("prepare rejects missing or invalid source metadata before reserving", async f =>
        {
            foreach (var (label, path, content) in new[]
                     {
                         ("invalid_project", f.ProjectPath, "<Project>"),
                         ("conditional_package", f.ProjectPath,
                             "<Project><PropertyGroup Condition=\"true\"><PackageId>DamianH.Piston</PackageId></PropertyGroup></Project>"),
                         ("missing_project", f.ProjectPath, null),
                         ("invalid_server", f.ServerPath, "{"),
                         ("missing_server_identifier", f.ServerPath, """{"packages": [{}]}"""),
                         ("missing_server", f.ServerPath, null),
                     })
            {
                await f.WriteSourceIdentity();
                if (content is null)
                {
                    File.Delete(path);
                }
                else
                {
                    await File.WriteAllTextAsync(path, content);
                }

                await f.Git("add", "-A");
                var sha = await f.Commit(label);
                await Expect.ThrowsAsync(() => f.PrepareAt(Day, sourceSha: sha, runId: 41), "package identity", label);
                await ExpectTags(f);
                Expect.Equal("", await f.Git("tag", "--list"), label);
                Expect.Equal(0, f.Http.Requests.Count, label);
            }
        });

        GitCase("prepare blocks incomplete or failed previous run", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 41);
            foreach (var (label, run) in new[]
                     {
                         ("in_progress", Run(41, "in_progress", null)), ("failed", Run(41, "completed", "failure")),
                         ("cancelled", Run(41, "completed", "cancelled")),
                         ("wrong_workflow", Run(41, "completed", "success", ".github/workflows/ci.yml")),
                         ("wrong_run_id", Run(99, "completed", "success")),
                     })
            {
                f.Http.Json(ReleaseFixture.ActionUrl(41), run);
                await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42), label: label);
                await ExpectTags(f, "v2026.1101.1-dev");
            }
        });

        GitCase("prepare blocks failed previous run after UTC rollover", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 41);
            f.Http.Json(ReleaseFixture.ActionUrl(41), Run(41, "completed", "failure"));
            await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), runId: 42));
            await ExpectTags(f, "v2026.1101.1-dev");
        });

        GitCase("prepare fails closed when GitHub status is unavailable", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 41);
            foreach (var response in new object[] { 503, new HttpRequestException("controlled outage") })
            {
                f.Http.Responses[ReleaseFixture.ActionUrl(41)] = response;
                await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42), label: response.ToString());
                await ExpectTags(f, "v2026.1101.1-dev");
            }
        });

        GitCase("prepare rejects clock before latest reservation", async f =>
        {
            await f.Tag(new ReleaseVersion(new(2026, 11, 2), 1, true), 41);
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42));
            await ExpectTags(f, "v2026.1102.1-dev");
        });

        GitCase("prepare accepts verified successful previous run", async f =>
        {
            var prior = new ReleaseVersion(Day, 1, true);
            await f.Tag(prior, 41);
            f.Success(41);
            f.PublicPackage(prior, f.SourceSha);
            var (version, reused) = await f.PrepareAt(Day, runId: 42, dev: false);
            Expect.Equal(false, reused);
            Expect.Equal("2026.1101.2", version.Version);
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2");
        });

        GitCase("prepare shares stable and dev counters", async f =>
        {
            var prior = new ReleaseVersion(Day, 4, false);
            await f.Tag(prior, 41);
            f.Success(41);
            f.PublicPackage(prior, f.SourceSha);
            f.PublicRelease(prior);
            f.DownloadAsset(ReleaseFixture.MakePackage(prior, f.SourceSha));
            var (version, reused) = await f.PrepareAt(Day, runId: 42, dev: true);
            Expect.Equal(false, reused);
            Expect.Equal("2026.1101.5-dev", version.Version);
            await ExpectTags(f, "v2026.1101.4", "v2026.1101.5-dev");
        });

        GitCase("prepare accepts verified expired stable run", async f =>
        {
            var prior = new ReleaseVersion(Day, 2, false);
            await f.Tag(prior, 41);
            f.Http.Responses[ReleaseFixture.ActionUrl(41)] = 404;
            f.PublicPackage(prior, f.SourceSha);
            f.PublicRelease(prior);
            f.DownloadAsset(ReleaseFixture.MakePackage(prior, f.SourceSha));
            var (version, reused) = await f.PrepareAt(new(2026, 11, 2), runId: 42);
            Expect.Equal(false, reused);
            Expect.Equal("2026.1102.1-dev", version.Version);
            await ExpectTags(f, "v2026.1101.2", "v2026.1102.1-dev");
        });

        GitCase("prepare blocks expired run without complete public evidence", async f =>
        {
            var prior = new ReleaseVersion(Day, 1, false);
            await f.Tag(prior, 41);
            f.Http.Responses[ReleaseFixture.ActionUrl(41)] = 404;
            foreach (var label in new[] { "missing_package", "missing_release", "missing_asset" })
            {
                f.PublicPackage(prior, f.SourceSha);
                f.PublicRelease(prior);
                switch (label)
                {
                    case "missing_package": f.Http.Responses[ReleaseFixture.PackageUrl(prior)] = 404; break;
                    case "missing_release": f.Http.Responses[ReleaseFixture.ReleaseUrl(prior)] = 404; break;
                    default: f.PublicRelease(prior, assets: []); break;
                }

                await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), runId: 42), label: label);
                await ExpectTags(f, "v2026.1101.1");
            }
        });

        GitCase("prepare blocks expired run when workflow API is inaccessible", async f =>
        {
            var prior = new ReleaseVersion(Day, 1, true);
            await f.Tag(prior, 41);
            f.Http.Responses[ReleaseFixture.ActionUrl(41)] = 404;
            f.Http.Responses[ReleaseFixture.WorkflowUrl] = 503;
            f.PublicPackage(prior, f.SourceSha);
            await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), runId: 42));
            await ExpectTags(f, "v2026.1101.1-dev");
            Expect.True(!f.Http.Requests.Contains(ReleaseFixture.PackageUrl(prior)), "package should not be requested");
        });

        GitCase("prepare blocks expired stable run with mismatched release asset", async f =>
        {
            var prior = new ReleaseVersion(Day, 1, false);
            await f.Tag(prior, 41);
            f.Http.Responses[ReleaseFixture.ActionUrl(41)] = 404;
            f.PublicPackage(prior, f.SourceSha);
            f.PublicRelease(prior);
            f.DownloadAsset(ReleaseFixture.MakePackage(prior, f.SourceSha, dll: "different executable"));
            await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), runId: 42));
            await ExpectTags(f, "v2026.1101.1");
        });

        GitCase("prepare blocks missing or mismatched public evidence", async f =>
        {
            var prior = new ReleaseVersion(Day, 1, false);
            await f.Tag(prior, 41);
            f.Success(41);
            foreach (var label in new[] { "missing_package", "wrong_commit", "missing_release", "missing_asset", "nuget_error" })
            {
                f.PublicPackage(prior, f.SourceSha);
                f.PublicRelease(prior);
                switch (label)
                {
                    case "missing_package": f.Http.Responses[ReleaseFixture.PackageUrl(prior)] = 404; break;
                    case "wrong_commit": f.PublicPackage(prior, new string('f', 40)); break;
                    case "missing_release": f.Http.Responses[ReleaseFixture.ReleaseUrl(prior)] = 404; break;
                    case "missing_asset": f.PublicRelease(prior, assets: []); break;
                    default: f.Http.Responses[ReleaseFixture.PackageUrl(prior)] = 503; break;
                }

                await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42), label: label);
                await ExpectTags(f, "v2026.1101.1");
            }
        });
    }
}

internal static partial class ReleaseTests
{
    private static void RegisterReservationHistoryTests()
    {
        GitCase("prepare blocks unresolved older reservation", async f =>
        {
            var newer = new ReleaseVersion(Day, 2, true);
            await f.Tag(new ReleaseVersion(Day, 1, true), 40);
            await f.Tag(newer, 41);
            f.Http.Json(ReleaseFixture.ActionUrl(40), Run(40, "completed", "failure"));
            f.Success(41);
            f.PublicPackage(newer, f.SourceSha);
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42));
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2-dev");
        });

        GitCase("prepare retry blocks unresolved older reservation", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 40);
            await f.Tag(new ReleaseVersion(Day, 2, true), 41);
            f.Http.Json(ReleaseFixture.ActionUrl(40), Run(40, "completed", "failure"));
            await Expect.ThrowsAsync(() => f.PrepareAt(new(2026, 11, 2), runId: 41));
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2-dev");
        });

        GitCase("verified history checkpoint avoids rechecking older publications", async f =>
        {
            var (first, _) = await f.PrepareAt(Day, runId: 40);
            f.Success(40);
            f.PublicPackage(first, f.SourceSha);
            var (second, _) = await f.PrepareAt(Day, runId: 41, dev: false);
            var body = GitFixture.TagBody(await f.Git("cat-file", "-p", $"refs/tags/{second.Tag}"));
            Expect.Contains($"\"verified_through\":\"{first.Tag}\"", body);

            f.Success(41);
            f.PublicPackage(second, f.SourceSha);
            f.PublicRelease(second);
            f.Http.Responses.Remove(ReleaseFixture.ActionUrl(40));
            f.Http.Responses.Remove(ReleaseFixture.PackageUrl(first));
            f.Http.Requests.Clear();
            f.DownloadAsset(ReleaseFixture.MakePackage(second, f.SourceSha));
            var (third, reused) = await f.PrepareAt(Day, runId: 42);
            Expect.Equal(false, reused);
            Expect.Equal("2026.1101.3-dev", third.Version);
            Expect.True(!f.Http.Requests.Contains(ReleaseFixture.ActionUrl(40)), "older run should not be checked");
            Expect.True(!f.Http.Requests.Contains(ReleaseFixture.PackageUrl(first)), "older package should not be checked");
        });

        GitCase("retry reuses a checkpoint after earlier publication was verified", async f =>
        {
            var (first, _) = await f.PrepareAt(Day, runId: 40);
            f.Success(40);
            f.PublicPackage(first, f.SourceSha);
            var (second, _) = await f.PrepareAt(Day, runId: 41);
            f.Http.Requests.Clear();
            var (retry, reused) = await f.PrepareAt(new(2026, 11, 2), runId: 41);
            Expect.Equal(true, reused);
            Expect.Equal(second, retry);
            Expect.Equal(0, f.Http.Requests.Count);
        });

        GitCase("prepare rejects a broken verified history checkpoint", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 40);
            var version = new ReleaseVersion(Day, 2, false);
            await f.Tag(version, 41, body: new JsonObject
            {
                ["piston_release"] = 1,
                ["run_id"] = 41,
                ["source_sha"] = f.SourceSha,
                ["version"] = version.Version,
                ["verified_through"] = "v2026.1031.1",
            }.ToJsonString());
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42));
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2");
        });

        GitCase("prepare rejects duplicate counter", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, false), 40);
            await f.Tag(new ReleaseVersion(Day, 1, true), 41);
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42));
            await ExpectTags(f, "v2026.1101.1", "v2026.1101.1-dev");
        });

        GitCase("prepare rejects duplicate run reservations", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 40);
            await f.Tag(new ReleaseVersion(Day, 2, true), 40);
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42));
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2-dev");
        });

        GitCase("prepare rejects lightweight calendar tag", async f =>
        {
            await f.Tag(new ReleaseVersion(Day, 1, true), 40, annotated: false);
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42));
            await ExpectTags(f, "v2026.1101.1-dev");
        });

        GitCase("read reservation rejects invalid metadata", async f =>
        {
            var sha = f.SourceSha;
            var cases = new[]
            {
                ("invalid_json", "not-json"),
                ("wrong_marker", $$"""{"piston_release": 0, "run_id": 40, "source_sha": "{{sha}}", "version": "2026.1101.2-dev"}"""),
                ("wrong_version", $$"""{"piston_release": 1, "run_id": 40, "source_sha": "{{sha}}", "version": "2026.1101.1-dev"}"""),
                ("wrong_source", $$"""{"piston_release": 1, "run_id": 40, "source_sha": "{{new string('f', 40)}}", "version": "2026.1101.4-dev"}"""),
                ("invalid_run", $$"""{"piston_release": 1, "run_id": "40", "source_sha": "{{sha}}", "version": "2026.1101.5-dev"}"""),
            };
            for (var index = 0; index < cases.Length; index++)
            {
                var version = new ReleaseVersion(Day, index + 1, true);
                await f.Tag(version, 40, body: cases[index].Item2);
                await Expect.ThrowsAsync(() => f.Tooling().ReadReservationAsync(f.Worktree, version.Tag, version), label: cases[index].Item1);
            }
        });

        GitCase("prepare rejects remote tag conflict without overwriting", async f =>
        {
            f.Intercept = async (file, arguments, cwd) =>
            {
                if (file != "git" || !arguments.Contains("tag") || !arguments.Contains("-a"))
                {
                    return null;
                }

                var result = await ReleaseTooling.RunProcessAsync(file, arguments, cwd);
                var tag = arguments[arguments.ToList().IndexOf("-a") + 1];
                await f.Git("--git-dir", f.Origin, "-c", "user.name=Other Publisher", "-c", "user.email=other@example.invalid",
                    "-c", "tag.gpgsign=false", "tag", "-a", tag, f.SourceSha, "-m", "competing reservation");
                return result;
            };
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 41));
            await ExpectTags(f, "v2026.1101.1-dev");
            var raw = await f.Git("--git-dir", f.Origin, "cat-file", "-p", "refs/tags/v2026.1101.1-dev");
            Expect.True(raw.EndsWith("competing reservation", StringComparison.Ordinal), "the competing reservation must survive");
        });
    }
}

internal static partial class ReleaseTests
{
    private static void RegisterRetiredReservationTests()
    {
        Case("only the known unpublished reservation is pinned for retirement", () =>
        {
            Expect.Sequence(["v2026.1101.1-dev"], ReleaseTooling.DefaultRetiredReservations.Keys);
            var retirement = ReleaseTooling.DefaultRetiredReservations["v2026.1101.1-dev"];
            Expect.Equal(41339003060, retirement.RunId);
            Expect.Equal("ce5c63d4da00690f40141435e59db7e8122b972f", retirement.SourceSha);
            Expect.Equal("Piston", retirement.PackageId);
            Expect.True(retirement.Reason.Length > 0, "a retirement needs a reason");
        });

        var prior = new ReleaseVersion(Day, 1, true);
        JsonObject FailedRun(GitFixture f) => new()
        {
            ["id"] = 41,
            ["path"] = ".github/workflows/release.yml@refs/heads/main",
            ["event"] = "workflow_run",
            ["head_sha"] = f.SourceSha,
            ["run_attempt"] = 1,
            ["status"] = "completed",
            ["conclusion"] = "failure",
        };
        JsonObject Step(string name, string conclusion) => new() { ["name"] = name, ["status"] = "completed", ["conclusion"] = conclusion };
        JsonObject Job(string conclusion = "failure", JsonArray? steps = null) => new()
        {
            ["name"] = "publish",
            ["status"] = "completed",
            ["conclusion"] = conclusion,
            ["steps"] = steps ?? [Step("Run NuGet/login@v1", "failure"), Step("Publish tool to NuGet.org", "skipped")],
        };
        JsonObject Jobs(int total, params JsonObject[] jobs) => new() { ["total_count"] = total, ["jobs"] = new JsonArray([.. jobs]) };
        void Evidence(GitFixture f)
        {
            f.Http.Json(ReleaseFixture.ActionUrl(41), FailedRun(f));
            f.Http.Json(ReleaseFixture.AttemptJobsUrl(41), Jobs(1, Job()));
            f.Http.Responses[ReleaseFixture.ReleaseUrl(prior)] = 404;
            f.Http.Responses[ReleaseFixture.PackageUrl(prior, "Piston")] = 404;
            f.Http.Responses[ReleaseFixture.PackageUrl(prior)] = 404;
        }

        void RetiredCase(string name, Func<GitFixture, Task> body) => GitCase(name, async f =>
        {
            await f.Tag(prior, 41);
            f.Retired[prior.Tag] = new RetiredReservation(41, f.SourceSha, "Piston", "Unpublished after the NuGet login failure.");
            Evidence(f);
            await body(f);
        });

        async Task Rejects(GitFixture f, string? label = null)
        {
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 42), label: label);
            await ExpectTags(f, prior.Tag);
        }

        RetiredCase("prepare retires verified failure without changing or reusing its tag", async f =>
        {
            var original = await f.Git("--git-dir", f.Origin, "cat-file", "-p", $"refs/tags/{prior.Tag}");
            var (version, reused) = await f.PrepareAt(Day, runId: 42);
            Expect.Equal("2026.1101.2-dev", version.Version);
            Expect.Equal(false, reused);
            Expect.Equal(original, await f.Git("--git-dir", f.Origin, "cat-file", "-p", $"refs/tags/{prior.Tag}"));
            var body = GitFixture.TagBody(await f.Git("--git-dir", f.Origin, "cat-file", "-p", $"refs/tags/{version.Tag}"));
            Expect.Contains($"\"verified_through\":\"{prior.Tag}\"", body);
            Expect.Sequence(
                [ReleaseFixture.ActionUrl(41), ReleaseFixture.AttemptJobsUrl(41), ReleaseFixture.ReleaseUrl(prior),
                 ReleaseFixture.PackageUrl(prior, "Piston"), ReleaseFixture.PackageUrl(prior)],
                f.Http.Requests);
        });

        RetiredCase("prepare continues history after verified retirement", async f =>
        {
            var (previous, _) = await f.PrepareAt(Day, runId: 42);
            f.Success(42);
            f.PublicPackage(previous, f.SourceSha);
            var (version, reused) = await f.PrepareAt(Day, runId: 43);
            Expect.Equal("2026.1101.3-dev", version.Version);
            Expect.Equal(false, reused);
            await ExpectTags(f, "v2026.1101.1-dev", "v2026.1101.2-dev", "v2026.1101.3-dev");
        });

        RetiredCase("prepare never reuses the retired run", async f =>
        {
            await Expect.ThrowsAsync(() => f.PrepareAt(Day, runId: 41), "retired");
            await ExpectTags(f, prior.Tag);
            Expect.Equal(0, f.Http.Requests.Count);
        });

        RetiredCase("prepare rejects unmatched retirement fingerprints", async f =>
        {
            foreach (var (label, tag, runId, sha) in new[]
                     {
                         ("wrong_run", prior.Tag, 99L, f.SourceSha), ("wrong_commit", prior.Tag, 41L, new string('f', 40)),
                         ("wrong_tag", "v2026.1101.2-dev", 41L, f.SourceSha),
                     })
            {
                f.Retired = new() { [tag] = new RetiredReservation(runId, sha, "Piston", "fixture") };
                await Rejects(f, label);
            }
        });

        RetiredCase("prepare rejects changed retired workflow runs", async f =>
        {
            foreach (var (label, changes) in new (string, (string, JsonNode?)[])[]
                     {
                         ("wrong_run_id", [("id", 99)]), ("wrong_workflow", [("path", ".github/workflows/ci.yml")]),
                         ("wrong_event", [("event", "workflow_dispatch")]), ("wrong_source", [("head_sha", new string('f', 40))]),
                         ("retried", [("run_attempt", 2)]), ("running", [("status", "in_progress"), ("conclusion", null)]),
                         ("succeeded", [("conclusion", "success")]), ("cancelled", [("conclusion", "cancelled")]),
                     })
            {
                Evidence(f);
                f.Http.Json(ReleaseFixture.ActionUrl(41), ReleaseFixture.With(FailedRun(f), changes));
                await Rejects(f, label);
            }
        });

        RetiredCase("prepare requires the failed login and skipped publish steps", async f =>
        {
            foreach (var (label, jobs) in new[]
                     {
                         ("missing_job", Jobs(0)), ("partial_jobs", Jobs(2, Job())), ("duplicate_job", Jobs(2, Job(), Job())),
                         ("job_succeeded", Jobs(1, Job("success"))),
                         ("login_succeeded", Jobs(1, Job(steps: [Step("Run NuGet/login@v1", "success"), Step("Publish tool to NuGet.org", "skipped")]))),
                         ("publish_ran", Jobs(1, Job(steps: [Step("Run NuGet/login@v1", "failure"), Step("Publish tool to NuGet.org", "success")]))),
                         ("missing_steps", Jobs(1, Job(steps: []))),
                     })
            {
                Evidence(f);
                f.Http.Json(ReleaseFixture.AttemptJobsUrl(41), jobs);
                await Rejects(f, label);
            }
        });

        RetiredCase("prepare fails closed when retirement evidence is missing or conflicting", async f =>
        {
            foreach (var (label, url, response) in new (string, string, object)[]
                     {
                         ("run_missing", ReleaseFixture.ActionUrl(41), 404), ("run_unavailable", ReleaseFixture.ActionUrl(41), 503),
                         ("jobs_missing", ReleaseFixture.AttemptJobsUrl(41), 404), ("jobs_unavailable", ReleaseFixture.AttemptJobsUrl(41), 503),
                         ("release_unavailable", ReleaseFixture.ReleaseUrl(prior), 403),
                         ("legacy_published", ReleaseFixture.PackageUrl(prior, "Piston"), "published"u8.ToArray()),
                         ("renamed_published", ReleaseFixture.PackageUrl(prior), "published"u8.ToArray()),
                         ("legacy_unavailable", ReleaseFixture.PackageUrl(prior, "Piston"), 503),
                         ("renamed_unavailable", ReleaseFixture.PackageUrl(prior), new HttpRequestException("controlled outage")),
                     })
            {
                Evidence(f);
                f.Http.Responses[url] = response;
                await Rejects(f, label);
            }

            Evidence(f);
            f.Http.Json(ReleaseFixture.ReleaseUrl(prior), new JsonObject { ["tag_name"] = prior.Tag });
            await Rejects(f, "release_exists");
        });
    }

    private static void RegisterPackageTests()
    {
        var version = new ReleaseVersion(Day, 2, true);
        var sha = new string('c', 40);
        void PackageCase(string name, Func<ReleaseFixture, string, Task> body) => Case(name, async () =>
        {
            using var fixture = new ReleaseFixture();
            await body(fixture, fixture.LocalPackage(version, sha));
        });

        Dictionary<string, string> Env(string package, string artifact, string attempt) => new()
        {
            ["GITHUB_REPOSITORY"] = Repo,
            ["RELEASE_PACKAGE"] = package,
            ["RELEASE_VERSION"] = version.Version,
            ["RELEASE_SOURCE_SHA"] = sha,
            ["RELEASE_REUSED"] = "false",
            ["RELEASE_ARTIFACT"] = artifact,
            ["GITHUB_RUN_ID"] = "41",
            ["GITHUB_RUN_ATTEMPT"] = attempt,
        };

        PackageCase("reconcile requires publish when package missing", async (f, path) =>
        {
            f.Http.Responses[ReleaseFixture.PackageUrl(version)] = 404;
            foreach (var retry in new[] { false, true })
            {
                Expect.Equal(true, await f.Tooling().ReconcileAsync(path, version, sha, Repo, retry));
            }
        });

        Case("retry eligibility requires a prior reservation or prior-attempt artifact", () =>
        {
            Expect.Equal(false, ReleaseTooling.IsReservedRetry(false, "piston-nuget-tool-41-1", 41, 1));
            Expect.Equal(false, ReleaseTooling.IsReservedRetry(false, "piston-nuget-tool-41-2", 41, 2));
            Expect.Equal(true, ReleaseTooling.IsReservedRetry(true, "piston-nuget-tool-41-2", 41, 2));
            Expect.Equal(true, ReleaseTooling.IsReservedRetry(false, "piston-nuget-tool-41-1", 41, 2));
        });

        Case("retry eligibility rejects unrelated or future artifacts", () =>
        {
            foreach (var (artifact, runId, attempt) in new[]
                     {
                         ("piston-nuget-tool-42-1", 41L, 2L), ("piston-nuget-tool-41-3", 41, 2), ("piston-nuget-tool-41-0", 41, 2),
                         ("piston-nuget-tool-0-1", 0, 1), ("piston-nuget-tool-41-1", 41, 0),
                     })
            {
                Expect.Throws(() => ReleaseTooling.IsReservedRetry(false, artifact, runId, attempt), label: artifact);
            }
        });

        PackageCase("reconcile command rejects duplicate for first reservation on a later attempt", async (f, path) =>
        {
            f.PublicPackage(version, sha);
            await Expect.ThrowsAsync(() => ReleaseTooling.RunCommandAsync(
                "reconcile", Env(path, "piston-nuget-tool-41-2", "2").GetValueOrDefault, f.Root, new StringWriter(), f.Tooling()));
        });

        PackageCase("reconcile command recovers an artifact from an earlier attempt", async (f, path) =>
        {
            f.PublicPackage(version, sha);
            var stdout = new StringWriter();
            await ReleaseTooling.RunCommandAsync(
                "reconcile", Env(Path.GetFileName(path), "piston-nuget-tool-41-1", "2").GetValueOrDefault, f.Root, stdout, f.Tooling());
            Expect.Equal("push=false\n", stdout.ToString().ReplaceLineEndings("\n"));
        });

        PackageCase("reconcile rejects unknown duplicate", async (f, path) =>
        {
            f.PublicPackage(version, sha);
            await Expect.ThrowsAsync(() => f.Tooling().ReconcileAsync(path, version, sha, Repo, false));
        });

        PackageCase("reconcile recovers identical reserved package", async (f, path) =>
        {
            f.PublicPackage(version, sha, signature: "nuget-side signature");
            Expect.Equal(false, await f.Tooling().ReconcileAsync(path, version, sha, Repo, true));
        });

        PackageCase("reconcile rejects mismatched published identity", async (f, path) =>
        {
            foreach (var (label, package) in new[]
                     {
                         ("wrong_commit", ReleaseFixture.MakePackage(version, new string('f', 40))),
                         ("wrong_repository", ReleaseFixture.MakePackage(version, sha, repo: "other/piston")),
                         ("wrong_nuspec_version", ReleaseFixture.MakePackage(version, sha, nuspecVersion: "2026.1101.3-dev")),
                         ("wrong_mcp_version", ReleaseFixture.MakePackage(version, sha, mcpVersion: "2026.1101.3-dev")),
                         ("old_nuspec_id", ReleaseFixture.MakePackage(version, sha, packageId: "Piston")),
                         ("old_mcp_identifier", ReleaseFixture.MakePackage(version, sha, identifier: "Piston")),
                         ("wrong_mcp_identifier", ReleaseFixture.MakePackage(version, sha, identifier: "Other")),
                     })
            {
                f.Http.Responses[ReleaseFixture.PackageUrl(version)] = package;
                await Expect.ThrowsAsync(() => f.Tooling().ReconcileAsync(path, version, sha, Repo, true), label: label);
            }
        });

        PackageCase("reconcile rejects same identity with different package content", async (f, path) =>
        {
            f.PublicPackage(version, sha, dll: "different executable");
            await Expect.ThrowsAsync(() => f.Tooling().ReconcileAsync(path, version, sha, Repo, true));
        });

        PackageCase("reconcile fails closed on HTTP errors", async (f, path) =>
        {
            foreach (var response in new object[] { 503, 403, new HttpRequestException("controlled outage") })
            {
                f.Http.Responses[ReleaseFixture.PackageUrl(version)] = response;
                await Expect.ThrowsAsync(() => f.Tooling().ReconcileAsync(path, version, sha, Repo, true), label: response.ToString());
            }
        });

        PackageCase("reconcile rejects invalid local package before network", async (f, path) =>
        {
            await File.WriteAllTextAsync(path, "not a NuGet package");
            await Expect.ThrowsAsync(() => f.Tooling().ReconcileAsync(path, version, sha, Repo, true));
            Expect.Equal(0, f.Http.Requests.Count);
        });

        PackageCase("verify public package accepts only matching release content", async (f, path) =>
        {
            f.PublicPackage(version, sha, signature: "nuget-side signature");
            await f.Tooling().VerifyPublicPackageAsync(path, version, sha, Repo);
            Expect.Sequence([ReleaseFixture.PackageUrl(version)], f.Http.Requests);
        });

        PackageCase("verify-package command checks the downloaded artifact", async (f, path) =>
        {
            f.PublicPackage(version, sha);
            await ReleaseTooling.RunCommandAsync("verify-package", Env(path, "", "1").GetValueOrDefault, f.Root, new StringWriter(), f.Tooling());
            Expect.Sequence([ReleaseFixture.PackageUrl(version)], f.Http.Requests);
        });

        PackageCase("verify public package rejects missing or mismatched release content", async (f, path) =>
        {
            foreach (var (label, response) in new (string, object)[]
                     {
                         ("missing", 404), ("wrong_source", ReleaseFixture.MakePackage(version, new string('f', 40))),
                         ("wrong_content", ReleaseFixture.MakePackage(version, sha, dll: "different executable")),
                     })
            {
                f.Http.Responses[ReleaseFixture.PackageUrl(version)] = response;
                await Expect.ThrowsAsync(() => f.Tooling().VerifyPublicPackageAsync(path, version, sha, Repo), label: label);
            }
        });

        RegisterGitHubReleaseTests();
    }
}

internal static partial class ReleaseTests
{
    private sealed class FakeGitHubRelease
    {
        public JsonObject? Release { get; set; }

        public Dictionary<string, byte[]> Assets { get; } = [];

        public List<string> Actions { get; } = [];

        public void Install(ReleaseFixture fixture, ReleaseVersion version)
        {
            fixture.Http.Responses[ReleaseFixture.ReleaseUrl(version)] =
                (Func<object>)(() => Release is null ? 404 : Encoding.UTF8.GetBytes(Release.ToJsonString()));
            fixture.Intercept = (file, arguments, _) =>
            {
                if (file != "gh" || arguments is not ["release", var action, ..])
                {
                    throw new InvalidOperationException($"Unexpected command: {file} {string.Join(' ', arguments)}");
                }

                Actions.Add(action);
                var list = arguments.ToList();
                switch (action)
                {
                    case "create":
                        Release = new JsonObject { ["tag_name"] = arguments[2], ["draft"] = false, ["prerelease"] = false, ["assets"] = new JsonArray() };
                        break;
                    case "upload":
                        var name = Path.GetFileName(arguments[3]);
                        Assets[name] = File.ReadAllBytes(arguments[3]);
                        Release!["assets"]!.AsArray().Add((JsonNode)new JsonObject { ["name"] = name, ["state"] = "uploaded" });
                        break;
                    case "download":
                        var pattern = arguments[list.IndexOf("--pattern") + 1];
                        File.WriteAllBytes(Path.Combine(arguments[list.IndexOf("--dir") + 1], pattern), Assets[pattern]);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected gh release action: {action}");
                }

                return Task.FromResult<string?>("");
            };
        }

        public void Existing(ReleaseVersion version, JsonArray? assets = null, params (string Name, JsonNode? Value)[] changes) =>
            Release = ReleaseFixture.With(new JsonObject
            {
                ["tag_name"] = version.Tag,
                ["draft"] = false,
                ["prerelease"] = false,
                ["assets"] = assets ?? [],
            }, changes);
    }

    private static void RegisterGitHubReleaseTests()
    {
        var version = new ReleaseVersion(Day, 3, false);
        var sha = new string('d', 40);
        void ReleaseCase(string name, Func<ReleaseFixture, FakeGitHubRelease, string, Task> body) => Case(name, async () =>
        {
            using var fixture = new ReleaseFixture();
            var gh = new FakeGitHubRelease();
            gh.Install(fixture, version);
            await body(fixture, gh, fixture.LocalPackage(version, sha));
        });

        Task Ensure(ReleaseFixture f, string path, ReleaseVersion? other = null) =>
            f.Tooling().EnsureGitHubReleaseAsync(path, other ?? version, sha, Repo);

        JsonArray Uploaded(string path, int count = 1) =>
            new([.. Enumerable.Range(0, count).Select(_ => (JsonNode)new JsonObject { ["name"] = Path.GetFileName(path), ["state"] = "uploaded" })]);

        ReleaseCase("ensure stable release create, upload and retry are idempotent", async (f, gh, path) =>
        {
            await Ensure(f, path);
            Expect.Equal(version.Tag, gh.Release!["tag_name"]!.GetValue<string>());
            Expect.Equal(Uploaded(path).ToJsonString(), gh.Release["assets"]!.ToJsonString());
            Expect.Sequence(File.ReadAllBytes(path), gh.Assets[Path.GetFileName(path)]);
            await Ensure(f, path);
            Expect.Sequence(["create", "upload", "download"], gh.Actions);
            Expect.Equal(1, gh.Release["assets"]!.AsArray().Count);
        });

        ReleaseCase("ensure stable release rejects mismatched existing asset", async (f, gh, path) =>
        {
            var name = Path.GetFileName(path);
            gh.Existing(version, Uploaded(path));
            var original = ReleaseFixture.MakePackage(version, sha, dll: "different executable");
            gh.Assets[name] = original;
            await Expect.ThrowsAsync(() => Ensure(f, path));
            Expect.Sequence(original, gh.Assets[name]);
            Expect.Sequence(["download"], gh.Actions);
        });

        ReleaseCase("ensure stable release rejects duplicate assets", async (f, gh, path) =>
        {
            gh.Existing(version, Uploaded(path, 2));
            await Expect.ThrowsAsync(() => Ensure(f, path));
            Expect.Equal(0, gh.Actions.Count);
        });

        ReleaseCase("ensure stable release rejects incomplete asset", async (f, gh, path) =>
        {
            gh.Existing(version, [new JsonObject { ["name"] = Path.GetFileName(path), ["state"] = "starter" }]);
            await Expect.ThrowsAsync(() => Ensure(f, path));
            Expect.Equal(0, gh.Actions.Count);
        });

        ReleaseCase("ensure stable release rejects invalid release state", async (f, gh, path) =>
        {
            foreach (var (label, change) in new (string, (string, JsonNode?))[]
                     {
                         ("draft", ("draft", true)), ("prerelease", ("prerelease", true)), ("wrong_tag", ("tag_name", "v2026.1101.99")),
                     })
            {
                gh.Existing(version, null, change);
                await Expect.ThrowsAsync(() => Ensure(f, path), label: label);
                Expect.Equal(0, gh.Actions.Count, label);
            }
        });

        ReleaseCase("ensure dev package never creates GitHub release", async (f, gh, _) =>
        {
            var dev = new ReleaseVersion(Day, 4, true);
            var path = f.LocalPackage(dev, sha);
            await Expect.ThrowsAsync(() => Ensure(f, path, dev));
            Expect.Equal(0, gh.Actions.Count);
            Expect.Equal(0, f.Http.Requests.Count);
        });
    }
}
