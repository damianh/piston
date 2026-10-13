// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.IO.Pipelines;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Piston.Engine;
using Piston.Engine.Execution;
using Piston.Hosting;
using Piston.Mcp;
using Xunit;

namespace Piston.Tests.Hosting;

public sealed class McpStdioHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-mcp-host-{Guid.NewGuid():N}");
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private (McpStdioHost Host, Task<int> Run) Start(McpStdioHostOptions options)
    {
        var host = new McpStdioHost(new McpStdioHostOptions
        {
            SolutionPath = options.SolutionPath,
            EngineOptions = options.EngineOptions,
            StartupError = options.StartupError,
            PresenceCheckInterval = options.PresenceCheckInterval,
            DeletionGracePeriod = options.DeletionGracePeriod,
            Input = _toServer.Reader.AsStream(),
            Output = _toClient.Writer.AsStream(),
            MonitorParentProcess = false,
            Log = _ => { },
        });
        return (host, host.RunAsync(CancellationToken.None));
    }

    private Task<McpClient> ConnectAsync() => McpClient.CreateAsync(
        new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()));

    private static async Task<(bool IsError, string Text)> CallAsync(McpClient client, string tool)
    {
        var result = await client.CallToolAsync(tool, new Dictionary<string, object?>());
        return (result.IsError == true, string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    private string CreateWorktree(string name)
    {
        var worktree = Path.Combine(_root, name);
        _ = Directory.CreateDirectory(Path.Combine(worktree, ".git"));
        var solution = Path.Combine(worktree, "App.slnx");
        File.WriteAllText(solution, "<Solution />");
        return solution;
    }

    [Fact]
    public async Task StartupError_EveryToolReportsIt_AndInputEofExits()
    {
        var (_, run) = Start(new McpStdioHostOptions { StartupError = "No solution found in '/nowhere'." });
        var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "verify");

        var (isError, text) = await CallAsync(client, "verify");
        Assert.True(isError);
        Assert.Contains("Piston could not start: No solution found in '/nowhere'.", text);
        Assert.Contains("dotnet test", text);

        await client.DisposeAsync();
        await _toServer.Writer.CompleteAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task OwnedElsewhere_ServesMcpButStartsNoEngine()
    {
        var solution = CreateWorktree("owned");
        var coordination = Path.Combine(_root, "coordination");
        var directory = new ExecutionCoordinationDirectory(coordination);
        directory.EnsureReady();
        using var owner = WorktreeOwnership.Acquire(directory, solution, "daemon");

        var (host, run) = Start(new McpStdioHostOptions
        {
            SolutionPath = solution,
            EngineOptions = new PistonOptions { SolutionPath = solution, ExecutionCoordinationDirectory = coordination },
        });
        var client = await ConnectAsync();

        foreach (var tool in new[] { "verify", "run_tests", "get_test_run", "get_test_results", "clear_results" })
        {
            var (isError, text) = await CallAsync(client, tool);
            Assert.True(isError, tool);
            Assert.Contains($"Piston owned by another process (PID {Environment.ProcessId}, daemon", text);
            Assert.Contains("Use `dotnet test` instead.", text);
        }

        Assert.Equal(EngineAvailabilityState.OwnedElsewhere, host.Availability.State);

        await client.DisposeAsync();
        await _toServer.Writer.CompleteAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(15)));

        // The rejected server never took (or released) ownership: the original owner still holds it.
        Assert.Equal(OwnershipLiveness.Live, Assert.Single(WorktreeOwnership.ListOwners(directory)).Liveness);
    }

    [Fact]
    public async Task WorktreeDeleted_ToolsReportIt_ThenExitsAndReleasesOwnership()
    {
        var solution = CreateWorktree("deleted");
        var coordination = Path.Combine(_root, "coordination");
        var directory = new ExecutionCoordinationDirectory(coordination);

        var (host, run) = Start(new McpStdioHostOptions
        {
            SolutionPath = solution,
            EngineOptions = new PistonOptions
            {
                SolutionPath = solution,
                ExecutionCoordinationDirectory = coordination,
                OwnerHost = McpStdioHost.OwnerHost,
            },
            PresenceCheckInterval = TimeSpan.FromMilliseconds(50),
            DeletionGracePeriod = TimeSpan.FromSeconds(2),
        });
        var client = await ConnectAsync();
        Assert.Equal("mcp", Assert.Single(WorktreeOwnership.ListOwners(directory)).Owner!.Host);

        Directory.Delete(Path.GetDirectoryName(solution)!, recursive: true);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (host.Availability.State != EngineAvailabilityState.WorktreeDeleted && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }

        var (isError, text) = await CallAsync(client, "get_test_run");
        Assert.True(isError);
        Assert.Contains("no longer exists", text);

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Empty(WorktreeOwnership.ListOwners(directory));
        await client.DisposeAsync();
    }
}
