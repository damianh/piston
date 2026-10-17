// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Tests.Smoke;

/// <summary>Skips unless <c>PISTON_DOGFOOD_SMOKE=1</c>.</summary>
public sealed class DogfoodSmokeFactAttribute : FactAttribute
{
    public DogfoodSmokeFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (Environment.GetEnvironmentVariable("PISTON_DOGFOOD_SMOKE") != "1")
        {
            Skip = "Set PISTON_DOGFOOD_SMOKE=1 to run the real-daemon dogfood smoke (builds measurements/fixtures/unit).";
        }
    }
}

/// <summary>
/// Bounded real-daemon smoke of the documented dogfood flow: <c>.piston.json</c> + <c>piston daemon</c>,
/// HTTP MCP <c>verify</c> for pass / reuse / failing test / compile error / edit during run,
/// <c>piston status</c>, startup conflicts and <c>piston stop</c>. Not a measurement.
/// Set <c>PISTON_SMOKE_EVIDENCE_DIR</c> to a durable directory of your choice to keep the transcript;
/// otherwise nothing is written outside the temporary workspace.
/// </summary>
public sealed partial class DogfoodSmokeTests : IDisposable
{
    private static readonly string PistonDll = Path.Combine(AppContext.BaseDirectory, "Piston.dll");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-dogfood-{Guid.NewGuid():N}");
    private readonly string _workspace;
    private readonly StringBuilder _transcript = new();
    private readonly List<Process> _processes = [];

    public DogfoodSmokeTests()
    {
        _workspace = Path.Combine(_root, "ws");
        CopyFixture(_workspace);
    }

    private string SolutionDir => Path.Combine(_workspace, "unit");
    private string PriceRules => Path.Combine(SolutionDir, "Pricing", "PriceRules.cs");

    [DogfoodSmokeFact]
    public async Task DocumentedFlow_VerifiesAndStopsCleanly()
    {
        var (mcpPort, webPort) = FreePorts();
        File.WriteAllText(Path.Combine(_workspace, ".piston.json"),
            $$"""{ "solution": "unit/Orders.slnx", "mcpPort": {{mcpPort}} }""");
        var original = File.ReadAllText(PriceRules);

        var daemon = StartPiston(_workspace, "daemon", "--web-port", webPort.ToString());
        await using var mcp = await ConnectAsync(mcpPort, daemon);

        var status = await RunPistonAsync(_workspace, "status");
        Assert.Equal(0, status.ExitCode);
        Assert.Contains($"MCP      : http://localhost:{mcpPort}", status.Output);
        Assert.Contains($"PID      : {daemon.Id}", status.Output);

        // Clean tree passes; a second call reuses the same run without building.
        var pass = await VerifyAsync(mcp);
        Assert.Contains("Verdict: Passed", pass);
        AssertFingerprintMatchesTree(pass);
        var reused = await VerifyAsync(mcp);
        Assert.True(reused.Contains("Reused: yes", StringComparison.Ordinal), reused);
        Assert.Equal(RunId(pass), RunId(reused));

        // Failing test.
        File.WriteAllText(PriceRules, original.Replace("KnownFailure = false", "KnownFailure = true"));
        var failed = await VerifyAsync(mcp);
        Assert.Contains("Verdict: TestsFailed", failed);
        Assert.Contains("ExistingFailure", failed);
        Assert.NotEqual(RunId(pass), RunId(failed));
        AssertFingerprintMatchesTree(failed);

        // Compile error.
        File.WriteAllText(PriceRules, original.Replace(
            "public static decimal Round", "public static int Broken => undefinedName;\n    public static decimal Round"));
        var broken = await VerifyAsync(mcp);
        Assert.Contains("Verdict: BuildFailed", broken);
        Assert.Contains("CS0103", broken);

        // Edit during execution: the result is bound to the final tree, or is honestly not a pass.
        File.WriteAllText(PriceRules, original);
        var entered = Path.Combine(_root, "test-entered");
        var release = Path.Combine(_root, "test-release");
        File.WriteAllText(Path.Combine(SolutionDir, "Pricing.Tests", "VerificationGateTests.cs"), $$"""
            using System;
            using System.IO;
            using System.Threading;
            using Xunit;
            public sealed class VerificationGateTests
            {
                [Fact]
                public void BlocksUntilSourceIsEdited()
                {
                    File.WriteAllText({{System.Text.Json.JsonSerializer.Serialize(entered)}}, "entered");
                    var deadline = DateTime.UtcNow.AddMinutes(2);
                    while (!File.Exists({{System.Text.Json.JsonSerializer.Serialize(release)}}) && DateTime.UtcNow < deadline)
                        Thread.Sleep(25);
                    Assert.True(File.Exists({{System.Text.Json.JsonSerializer.Serialize(release)}}));
                }
            }
            """);
        var during = VerifyAsync(mcp);
        try
        {
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (!File.Exists(entered))
            {
                Assert.False(during.IsCompleted, "Verification completed before the controlled test entered.");
                Assert.True(DateTime.UtcNow < deadline, "The controlled test never entered.");
                await Task.Delay(25);
            }

            Assert.False(during.IsCompleted, "Verification must still be running when the edit is made.");
            File.WriteAllText(PriceRules, original + "\n// edited during run\n");
        }
        finally
        {
            File.WriteAllText(release, "release");
        }
        var afterEdit = await during;
        if (afterEdit.Contains("Verdict: Passed"))
        {
            AssertFingerprintMatchesTree(afterEdit);
        }
        else
        {
            Assert.Contains("Verdict: Stale", afterEdit);
        }

        // Startup conflicts: the same worktree is refused; an occupied MCP port fails fast and visibly.
        var second = await RunPistonAsync(_workspace, "daemon", "--web-port", FreePort().ToString());
        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("error:", second.Output);

        var other = Path.Combine(_root, "other");
        CopyFixture(other);
        using (var occupied = new TcpListener(IPAddress.Loopback, 0))
        {
            occupied.Start();
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            var conflict = await RunPistonAsync(other, "daemon", "unit/Orders.slnx", "--mcp-port", port.ToString(),
                "--web-port", FreePort().ToString());
            Assert.Equal(1, conflict.ExitCode);
            Assert.Contains("error: Could not start the MCP server", conflict.Output);
            // Endpoints bind before the startup run, so the conflicting daemon never built anything.
            Assert.Empty(Directory.EnumerateDirectories(other, "obj", SearchOption.AllDirectories));
        }

        // Clean teardown.
        var stop = await RunPistonAsync(_workspace, "stop");
        Assert.Equal(0, stop.ExitCode);
        Assert.True(daemon.WaitForExit(TimeSpan.FromSeconds(10)), "Daemon did not exit after piston stop.");
        using var released = new TcpListener(IPAddress.Loopback, mcpPort);
        released.Start();
        var after = await RunPistonAsync(_workspace, "status");
        Assert.Equal(1, after.ExitCode);
        var stopAgain = await RunPistonAsync(_workspace, "stop");
        Assert.Equal(0, stopAgain.ExitCode);
        Assert.Contains("No daemon running", stopAgain.Output);
    }

    private static async Task<McpClient> ConnectAsync(int port, Process daemon)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            Assert.False(daemon.HasExited, "Daemon exited before MCP was reachable.");
            try
            {
                return await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/"),
                }));
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private async Task<string> VerifyAsync(McpClient mcp)
    {
        var result = await mcp.CallToolAsync("verify", new Dictionary<string, object?> { ["timeoutSeconds"] = 300 });
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Log($"verify ->\n{text}\n");
        return text;
    }

    private void AssertFingerprintMatchesTree(string verifyText)
    {
        var expected = SourceFingerprinter.Compute(SolutionDir);
        Assert.Contains($"Source: {expected.Algorithm}:{expected.Value}", verifyText);
    }

    private static string RunId(string verifyText) => RunIdRegex().Match(verifyText).Groups[1].Value;

    [GeneratedRegex(@"^Run: (\S+)", RegexOptions.Multiline)]
    private static partial Regex RunIdRegex();

    private Process StartPiston(string workingDirectory, params string[] args)
    {
        var process = Process.Start(CreateStartInfo(workingDirectory, args))
            ?? throw new InvalidOperationException("Failed to start piston.");
        _processes.Add(process);
        var name = $"piston {string.Join(' ', args)} [{process.Id}]";
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { Log($"{name}: {e.Data}"); } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Log($"{name}: {e.Data}"); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private async Task<(int ExitCode, string Output)> RunPistonAsync(string workingDirectory, params string[] args)
    {
        using var process = Process.Start(CreateStartInfo(workingDirectory, args))
            ?? throw new InvalidOperationException("Failed to start piston.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"piston {string.Join(' ', args)} did not exit within 2 minutes.");
        }

        var output = await stdout + await stderr;
        Log($"piston {string.Join(' ', args)} -> exit {process.ExitCode}\n{output}");
        return (process.ExitCode, output);
    }

    private ProcessStartInfo CreateStartInfo(string workingDirectory, string[] args)
    {
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(PistonDll);
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }

        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        // Keep machine-wide admission state separate from any daemons the developer is running.
        info.Environment["PISTON_EXECUTION_DIRECTORY"] = Path.Combine(_root, "execution");
        return info;
    }

    private void Log(string line)
    {
        lock (_transcript)
        {
            _ = _transcript.AppendLine($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {line}");
        }
    }

    internal static void CopyFixture(string destination)
    {
        var fixtures = Path.Combine(FindRepoRoot(), "measurements", "fixtures");
        _ = Directory.CreateDirectory(destination);
        foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props", "global.json" })
        {
            File.Copy(Path.Combine(fixtures, file), Path.Combine(destination, file));
        }

        var source = Path.Combine(fixtures, "unit");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            var target = Path.Combine(destination, "unit", relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Piston.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    internal static (int McpPort, int WebPort) FreePorts()
    {
        using var mcp = new TcpListener(IPAddress.Loopback, 0);
        using var web = new TcpListener(IPAddress.Loopback, 0);
        mcp.Start();
        web.Start();
        return (((IPEndPoint)mcp.LocalEndpoint).Port, ((IPEndPoint)web.LocalEndpoint).Port);
    }

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(TimeSpan.FromSeconds(10));
                }
            }
            catch (InvalidOperationException) { }

            process.Dispose();
        }

        if (Environment.GetEnvironmentVariable("PISTON_SMOKE_EVIDENCE_DIR") is { Length: > 0 } artifacts)
        {
            _ = Directory.CreateDirectory(artifacts);
            File.WriteAllText(Path.Combine(artifacts, $"dogfood-smoke-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.log"),
                _transcript.ToString());
        }

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
