// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// Acceptance test for integration-test resource scheduling: the same test project, which binds a fixed port,
/// runs at the same time from two worktrees through two runners that share the machine's coordination
/// directory. The runs queue for the declared port instead of colliding, and the wait is reported.
/// </summary>
public sealed class TestResourceSchedulingTests : IAsyncLifetime
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private string _root = string.Empty;
    private int _port;
    private readonly string[] _worktrees = new string[2];

    public async ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("piston-resources-").FullName;
        _port = FreePort();
        for (var i = 0; i < _worktrees.Length; i++)
        {
            _worktrees[i] = Directory.CreateDirectory(Path.Combine(_root, $"wt{i}")).FullName;
            var projectDir = Directory.CreateDirectory(Path.Combine(_worktrees[i], "tests", "Port.Tests")).FullName;
            await WriteFixedPortProjectAsync(projectDir, _port);
            _ = await TestProcess.RunDotnetAsync("restore", projectDir);
            _ = await TestProcess.RunDotnetAsync("build --no-restore", projectDir);
        }
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task SameFixedPortProject_InTwoWorktrees_QueuesInsteadOfColliding()
    {
        var coordination = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        coordination.EnsureReady();
        var rules = new[] { new TestResourceRule("tests/**/Port.Tests.csproj") { Exclusive = [$"port:{_port}"] } };
        var waits = new ConcurrentQueue<(string Project, TestResourceWait Wait)>();

        Task<TestRunResult> Run(string worktree)
        {
            var solution = Path.Combine(worktree, "App.slnx");
            var project = Path.Combine(worktree, "tests", "Port.Tests", "Port.Tests.csproj");
            var resolver = new TestResourceResolver(new TestResourceInference(solution, rules, _ => null), () => [project]);
            var gate = new MachineResourceCoordinator(coordination, resolver.Resolve, worktree, TimeSpan.FromMilliseconds(50));
            var sut = new TestRunnerService(new ProcessTestExecutionStrategy(new TrxResultParser()), null, gate);
            return sut.RunTestsAsync(
                solution,
                [project],
                filter: null,
                collectCoverage: false,
                new TestRunCallbacks { OnProjectWaiting = (p, w) => waits.Enqueue((p, w)) },
                _ct);
        }

        var results = await Task.WhenAll(_worktrees.Select(Run)).WaitAsync(TimeSpan.FromMinutes(3), _ct);

        foreach (var result in results)
        {
            var test = Assert.Single(result.Suites.SelectMany(s => s.Tests));
            Assert.True(test.Status == TestStatus.Passed, $"{test.FullyQualifiedName}: {test.ErrorMessage}");
            var project = Assert.Single(result.Projects);
            Assert.Equal([$"port:{_port}"], project.Resources!.Needs.Keys);
            Assert.Equal(TestResourceWaitState.Acquired, project.Resources.Wait!.State);
        }

        var (waiter, wait) = Assert.Single(waits.DistinctBy(w => w.Project));
        var blocker = Assert.Single(wait.BlockedBy);
        Assert.Equal($"port:{_port}", blocker.Resource);
        Assert.Equal(TestResourceBlockerKind.Held, blocker.Kind);
        Assert.NotEqual(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(waiter))), blocker.Worktree);
        Assert.Contains(results, r => r.Projects[0].ProjectPath == waiter && r.Projects[0].Resources!.Wait!.Waited > TimeSpan.Zero);
        Assert.Empty(Directory.GetFiles(coordination.ResourceLeasesDirectory));
        Assert.Empty(Directory.GetFiles(coordination.ResourceQueueDirectory));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task WriteFixedPortProjectAsync(string dir, int port)
    {
        await File.WriteAllTextAsync(Path.Combine(dir, "Port.Tests.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <IsTestProject>true</IsTestProject>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
                <PackageReference Include="xunit" Version="2.*" />
                <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
              </ItemGroup>
            </Project>
            """);

        // Like a test host on a fixed port: a second concurrent run fails to bind.
        await File.WriteAllTextAsync(Path.Combine(dir, "Tests.cs"), $$"""
            using System.Net;
            using System.Net.Sockets;
            using Xunit;
            namespace Port.Tests;
            public class Tests
            {
                [Fact]
                public void HoldsFixedPort()
                {
                    var listener = new TcpListener(IPAddress.Loopback, {{port}});
                    listener.Start();
                    try { Thread.Sleep(2000); }
                    finally { listener.Stop(); }
                }
            }
            """);
    }
}
