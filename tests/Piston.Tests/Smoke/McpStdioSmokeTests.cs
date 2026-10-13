// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Piston.Tests.Smoke;

/// <summary>
/// Opt-in (<c>PISTON_DOGFOOD_SMOKE=1</c>) end-to-end checks of <c>piston mcp</c> the way an agent host runs it:
/// one stdio process per session with cwd = the session worktree.
/// </summary>
public sealed class McpStdioSmokeTests : IDisposable
{
    private static readonly string PistonDll = Path.Combine(AppContext.BaseDirectory, "Piston.dll");

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-mcp-smoke-{Guid.NewGuid():N}");
    private readonly StringBuilder _transcript = new();
    private readonly List<Process> _processes = [];
    private readonly List<McpClient> _clients = [];

    private string CreateWorktree(string name)
    {
        var worktree = Path.Combine(_root, name);
        DogfoodSmokeTests.CopyFixture(worktree);
        _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        File.WriteAllText(Path.Combine(worktree, ".piston.json"), """{ "solution": "unit/Orders.slnx" }""");
        return worktree;
    }

    [DogfoodSmokeFact]
    public async Task TwoWorktrees_RunConcurrently_AndASecondSessionGetsOwnershipError()
    {
        var a = CreateWorktree("wt-a");
        var b = CreateWorktree("wt-b");
        var (processA, clientA) = await StartMcpAsync(a);
        var (processB, clientB) = await StartMcpAsync(b);

        var results = await Task.WhenAll(CallAsync(clientA, "verify"), CallAsync(clientB, "verify"));
        Assert.False(results[0].IsError, results[0].Text);
        Assert.False(results[1].IsError, results[1].Text);
        Assert.Contains("Verdict: Passed", results[0].Text);
        Assert.Contains("Verdict: Passed", results[1].Text);
        Assert.Contains($"Solution: {Path.Combine(a, "unit", "Orders.slnx")}", results[0].Text);
        Assert.Contains($"Solution: {Path.Combine(b, "unit", "Orders.slnx")}", results[1].Text);

        var (processC, clientC) = await StartMcpAsync(a);
        var conflict = await CallAsync(clientC, "verify");
        Assert.True(conflict.IsError, conflict.Text);
        Assert.Contains($"Piston owned by another process (PID {processA.Id}, mcp", conflict.Text);
        Assert.Contains("dotnet test", conflict.Text);

        var status = await RunPistonAsync(_root, "status", "--all");
        Assert.Contains($"PID {processA.Id} (mcp)", status.Output);
        Assert.Contains($"PID {processB.Id} (mcp)", status.Output);
        Assert.DoesNotContain($"PID {processC.Id}", status.Output);

        await CloseAsync(processC, clientC);
        await CloseAsync(processA, clientA);
        await CloseAsync(processB, clientB);

        Assert.Contains("No Piston process owns a worktree.", (await RunPistonAsync(_root, "status", "--all")).Output);
    }

    [DogfoodSmokeFact]
    public async Task WorktreeDeletion_ReportsAndExits_ReleasingOwnership()
    {
        var worktree = CreateWorktree("wt-deleted");
        var (process, client) = await StartMcpAsync(worktree);
        var pass = await CallAsync(client, "verify");
        Assert.Contains("Verdict: Passed", pass.Text);

        Directory.Delete(worktree, recursive: true);

        using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(exitCts.Token);
        Assert.Equal(0, process.ExitCode);
        lock (_transcript)
        {
            Assert.Contains("no longer exists", _transcript.ToString());
        }

        Assert.Contains("No Piston process owns a worktree.", (await RunPistonAsync(_root, "status", "--all")).Output);
    }

    [DogfoodSmokeFact]
    public async Task HttpDaemon_WorktreeDeletion_ShutsDownAndFreesPorts()
    {
        var worktree = CreateWorktree("wt-daemon");
        var mcpPort = FreePort();
        var webPort = FreePort();
        File.WriteAllText(Path.Combine(worktree, ".piston.json"),
            $$"""{ "solution": "unit/Orders.slnx", "mcpPort": {{mcpPort}} }""");
        var daemon = StartPiston(worktree, ["daemon", "--web-port", webPort.ToString()]);
        await WaitForPortAsync(mcpPort, daemon);

        Directory.Delete(worktree, recursive: true);

        using var exitCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await daemon.WaitForExitAsync(exitCts.Token);
        AssertPortFree(mcpPort);
        AssertPortFree(webPort);
        Assert.Contains("No Piston process owns a worktree.", (await RunPistonAsync(_root, "status", "--all")).Output);
    }

    [DogfoodSmokeFact]
    public async Task StdinEof_MidRun_ExitsWithoutOrphanedChildren()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // Descendant tracking reads /proc.
        }

        var worktree = CreateWorktree("wt-eof");
        var (process, client) = await StartMcpAsync(worktree);

        // The startup run is building/testing; capture the dotnet children it spawned.
        var seen = new Dictionary<int, string>();
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (!seen.Values.Any(IsBuildOrTest) && DateTime.UtcNow < deadline)
        {
            foreach (var (pid, cmd) in Descendants(process.Id))
            {
                seen[pid] = cmd;
            }

            await Task.Delay(50);
        }

        Assert.Contains(seen.Values, IsBuildOrTest);
        Log($"children mid-run: {string.Join(" | ", seen.Select(kv => $"{kv.Key}: {kv.Value}"))}");

        await CloseAsync(process, client);

        await Task.Delay(1000);
        var survivors = seen
            .Where(kv => IsAlive(kv.Key, kv.Value) && !IsSdkBuildServer(kv.Value))
            .Select(kv => $"{kv.Key}: {kv.Value}")
            .ToList();
        Assert.True(survivors.Count == 0, "Orphaned children: " + string.Join(" | ", survivors));
    }

    private async Task<(Process Process, McpClient Client)> StartMcpAsync(string worktree)
    {
        var info = CreateStartInfo(worktree, ["mcp"]);
        info.RedirectStandardInput = true;
        var process = Process.Start(info) ?? throw new InvalidOperationException("Failed to start piston mcp.");
        _processes.Add(process);
        var name = $"piston mcp [{process.Id}] {Path.GetFileName(worktree)}";
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { Log($"{name}: {e.Data}"); } };
        process.BeginErrorReadLine();

        var client = await McpClient.CreateAsync(
            new StreamClientTransport(process.StandardInput.BaseStream, process.StandardOutput.BaseStream));
        _clients.Add(client);
        return (process, client);
    }

    private async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool)
    {
        var result = await client.CallToolAsync(tool, new Dictionary<string, object?>());
        var text = string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Log($"{tool} -> isError={result.IsError}\n{text}");
        return (result.IsError == true, text);
    }

    private static async Task CloseAsync(Process process, McpClient client)
    {
        await client.DisposeAsync();
        try { process.StandardInput.Close(); } catch (IOException) { }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(cts.Token);
        Assert.Equal(0, process.ExitCode);
    }

    private static IEnumerable<(int Pid, string Command)> Descendants(int root)
    {
        var parents = new Dictionary<int, int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }

            try
            {
                var stat = File.ReadAllText(Path.Combine(dir, "stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                parents[pid] = int.Parse(fields[1]);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var frontier = new Queue<int>([root]);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var child in parents.Where(p => p.Value == current).Select(p => p.Key))
            {
                frontier.Enqueue(child);
                yield return (child, CommandLine(child));
            }
        }
    }

    private static string CommandLine(int pid)
    {
        try
        {
            return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static bool IsBuildOrTest(string command) =>
        command.Contains(" build", StringComparison.Ordinal)
        || command.Contains("testhost", StringComparison.OrdinalIgnoreCase)
        || command.Contains(" test", StringComparison.Ordinal)
        || command.Contains("MSBuild.dll", StringComparison.Ordinal);

    // A PID may be reused; only count it if it still runs the same command.
    private static bool IsAlive(int pid, string command) =>
        Directory.Exists($"/proc/{pid}") && CommandLine(pid) == command;

    // MSBuild worker nodes and the Roslyn compiler server are designed to outlive the build that started them.
    private static bool IsSdkBuildServer(string command) =>
        command.Contains("nodeReuse:true", StringComparison.OrdinalIgnoreCase)
        || command.Contains("VBCSCompiler", StringComparison.Ordinal)
        || command.Contains("MSBuild.dll /nologo /nodemode", StringComparison.OrdinalIgnoreCase);

    private Process StartPiston(string workingDirectory, string[] args)
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
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        await process.WaitForExitAsync(cts.Token);
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
        info.Environment["PISTON_EXECUTION_DIRECTORY"] = Path.Combine(_root, "execution");
        return info;
    }

    private static async Task WaitForPortAsync(int port, Process owner)
    {
        var deadline = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            Assert.False(owner.HasExited, "Daemon exited before listening.");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    private static void AssertPortFree(int port)
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private void Log(string line)
    {
        lock (_transcript)
        {
            _ = _transcript.AppendLine($"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {line}");
        }
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            try { _ = client.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5)); } catch { /* best effort */ }
        }

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
            File.WriteAllText(Path.Combine(artifacts, $"mcp-stdio-smoke-{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}.log"),
                _transcript.ToString());
        }

        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
