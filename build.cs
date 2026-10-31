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
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
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

Target("default", dependsOn: ["test", "check-formatting"]);

await RunTargetsAndExitAsync(args,
    messageOnly: ex => ex is SimpleExec.ExitCodeException or SmokeTestException or PackCheckException);

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
        if (expectedServerVersion is not null && client.ServerInfo.Version != expectedServerVersion)
        {
            throw new SmokeTestException($"MCP server version is {client.ServerInfo.Version}, expected {expectedServerVersion}.");
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
    const string oldVersion = "0.0.1-distribution.1";
    const string newVersion = "0.0.2-distribution.2";
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
            ["tool", operation, "Piston", "--version", version, "--tool-path", Path.GetDirectoryName(tool)!,
                    "--source", feed], configureEnvironment: Configure);
        Console.WriteLine(output + error);
    }

    async Task ProbeUpdateAsync(string mode)
    {
        var exitCode = 0;
        var (output, error) = await ReadAsync("dotnet",
            ["tool", "update", "Piston", "--version", newVersion, "--tool-path", Path.GetDirectoryName(tool)!,
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

    async Task CheckVersionAsync()
    {
        var (version, error) = await ReadAsync(tool, ["--version"]);
        if (!version.Contains(newVersion, StringComparison.Ordinal))
        {
            throw new SmokeTestException($"Restarted CLI is not {newVersion}: {version}{error}");
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
            ["dnx", $"Piston@{oldVersion}", "--source", feed, "--", "--help"],
            configureEnvironment: Configure);
        if (!help.Contains("mcp", StringComparison.Ordinal) || !help.Contains("daemon", StringComparison.Ordinal))
        {
            throw new SmokeTestException($"dnx did not forward --help: {help}{helpError}");
        }

        await SmokeMcpAsync("dotnet",
            ["dnx", $"Piston@{oldVersion}", "--source", feed, "--", "mcp", samples], environment,
            expectedServerVersion: "0.0.1.0");

        tool = Path.Combine(root, "mcp-tool", OperatingSystem.IsWindows() ? "piston.exe" : "piston");
        await ToolCommandAsync("install", oldVersion);
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
                || client.ServerInfo.Version != "0.0.1.0")
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
        await CheckVersionAsync();
        await SmokeMcpAsync(environment: environment, expectedServerVersion: "0.0.2.0");

        tool = Path.Combine(root, "daemon-tool", OperatingSystem.IsWindows() ? "piston.exe" : "piston");
        await ToolCommandAsync("install", oldVersion);
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
        await CheckVersionAsync();
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
    var source = Directory.GetFiles(directory, "Piston.*.nupkg").Single();
    using var package = ZipFile.OpenRead(source);
    using var stream = package.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal)).Open();
    var version = XDocument.Load(stream).Descendants().Single(e => e.Name.LocalName == "version").Value;
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
        var url = $"https://api.nuget.org/v3-flatcontainer/piston/{version.ToLowerInvariant()}/piston.{version.ToLowerInvariant()}.nupkg";
        var destination = Path.Combine(root, $"Piston.{version}.nupkg");
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
            ["dnx", $"Piston@{version}", "--source", "https://api.nuget.org/v3/index.json", "--", "mcp", samples],
            new Dictionary<string, string?>
            {
                ["DOTNET_CLI_HOME"] = Path.Combine(root, "cli-home"),
                ["NUGET_PACKAGES"] = Path.Combine(root, "nuget-packages"),
                ["DOTNET_NOLOGO"] = "true",
            });
        Console.WriteLine($"PASS: NuGet.org metadata and dnx MCP for Piston {version}.");
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
static void CheckPackageContents(string directory, string? expectedVersion = null)
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
    var nuspec = archive.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
    using var nuspecStream = nuspec.Open();
    var specification = XDocument.Load(nuspecStream);
    var ns = specification.Root!.Name.Namespace;
    var metadata = specification.Root.Element(ns + "metadata")!;
    var version = metadata.Element(ns + "version")!.Value;
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
    var json = JsonNode.Parse(reader.ReadToEnd())!;
    const string schemaUrl = "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json";
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var schema = JsonSchema.FromText(http.GetStringAsync(schemaUrl).GetAwaiter().GetResult());
    if (!schema.Evaluate(json).IsValid)
    {
        problems.Add("server.json does not conform to the pinned MCP schema");
    }

    var serverPackage = json["packages"]!.AsArray().Single()!;
    if (json["$schema"]!.GetValue<string>() != schemaUrl
        || json["name"]!.GetValue<string>() != "io.github.damianh/piston"
        || json["version"]!.GetValue<string>() != version
        || serverPackage["version"]!.GetValue<string>() != version
        || serverPackage["identifier"]!.GetValue<string>() != "Piston"
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
