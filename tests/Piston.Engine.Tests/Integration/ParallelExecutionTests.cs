using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// Integration tests for parallel test execution via TestProcessPool.
/// Creates real temp xUnit projects, restores, builds, then runs via TestRunnerService with injected pool.
/// </summary>
public sealed class ParallelExecutionTests : IAsyncLifetime
{
    private string _root = string.Empty;

    // Project 1: Alpha.Tests
    private string _alphaTestsDir = string.Empty;
    private string _alphaTestsCsproj = string.Empty;

    // Project 2: Beta.Tests
    private string _betaTestsDir = string.Empty;
    private string _betaTestsCsproj = string.Empty;

    // Project 3: Gamma.Tests
    private string _gammaTestsDir = string.Empty;
    private string _gammaTestsCsproj = string.Empty;

    public async Task InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("piston-parallel-").FullName;

        _alphaTestsDir  = Directory.CreateDirectory(Path.Combine(_root, "Alpha.Tests")).FullName;
        _betaTestsDir   = Directory.CreateDirectory(Path.Combine(_root, "Beta.Tests")).FullName;
        _gammaTestsDir  = Directory.CreateDirectory(Path.Combine(_root, "Gamma.Tests")).FullName;

        _alphaTestsCsproj = Path.Combine(_alphaTestsDir, "Alpha.Tests.csproj");
        _betaTestsCsproj  = Path.Combine(_betaTestsDir,  "Beta.Tests.csproj");
        _gammaTestsCsproj = Path.Combine(_gammaTestsDir, "Gamma.Tests.csproj");

        await WriteTestProject(_alphaTestsCsproj, _alphaTestsDir, "Alpha", passCount: 2);
        await WriteTestProject(_betaTestsCsproj,  _betaTestsDir,  "Beta",  passCount: 3);
        await WriteTestProject(_gammaTestsCsproj, _gammaTestsDir, "Gamma", passCount: 1);

        // Restore and build all projects
        foreach (var dir in new[] { _alphaTestsDir, _betaTestsDir, _gammaTestsDir })
        {
            await RunDotnetAsync("restore", dir);
            await RunDotnetAsync("build --no-restore", dir);
        }
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task RunWithPool_AllProjectsResults_AreIncluded()
    {
        var parser = new TrxResultParser();
        using var pool = new TestProcessPool(2, 50, parser);
        var sut = new TestRunnerService(parser, pool);

        var projectPaths = new[] { _alphaTestsCsproj, _betaTestsCsproj, _gammaTestsCsproj }.ToList();

        var result = await sut.RunTestsAsync(
            solutionPath: _root,
            testProjectPaths: projectPaths,
            filter: null,
            collectCoverage: false,
            onProgress: null,
            onProjectCompleted: null,
            CancellationToken.None);

        Assert.NotEmpty(result.Suites);
        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();

        // 2 + 3 + 1 = 6 tests total
        Assert.Equal(6, allTests.Count);
        Assert.All(allTests, t => Assert.Equal(TestStatus.Passed, t.Status));
    }

    [Fact]
    public async Task RunWithPool_PerProjectCallback_FiresForEachProject()
    {
        var completedProjects = new System.Collections.Concurrent.ConcurrentBag<string>();
        var parser = new TrxResultParser();
        using var pool = new TestProcessPool(2, 50, parser);
        var sut = new TestRunnerService(parser, pool);

        var projectPaths = new[] { _alphaTestsCsproj, _betaTestsCsproj, _gammaTestsCsproj }.ToList();

        await sut.RunTestsAsync(
            solutionPath: _root,
            testProjectPaths: projectPaths,
            filter: null,
            collectCoverage: false,
            onProgress: null,
            onProjectCompleted: r => completedProjects.Add(r.ProjectPath),
            CancellationToken.None);

        Assert.Equal(3, completedProjects.Count);
        Assert.Contains(_alphaTestsCsproj, completedProjects);
        Assert.Contains(_betaTestsCsproj, completedProjects);
        Assert.Contains(_gammaTestsCsproj, completedProjects);
    }

    [Fact]
    public async Task RunWithPoolSize1_ProducesIdenticalResults_ToSequential()
    {
        var parser = new TrxResultParser();
        var projectPaths = new[] { _alphaTestsCsproj, _betaTestsCsproj, _gammaTestsCsproj }.ToList();

        // Run sequentially (no pool)
        var sequentialSut = new TestRunnerService(parser);
        var sequentialResult = await sequentialSut.RunTestsAsync(
            solutionPath: _root,
            testProjectPaths: projectPaths,
            filter: null,
            collectCoverage: false,
            onProgress: null,
            CancellationToken.None);

        // Run with pool size 1 (single-slot pool but still uses pool path for 3 projects)
        using var pool = new TestProcessPool(1, 50, parser);
        var poolSut = new TestRunnerService(parser, pool);
        var poolResult = await poolSut.RunTestsAsync(
            solutionPath: _root,
            testProjectPaths: projectPaths,
            filter: null,
            collectCoverage: false,
            onProgress: null,
            onProjectCompleted: null,
            CancellationToken.None);

        // Both should return the same total test count
        var seqTests  = sequentialResult.Suites.SelectMany(s => s.Tests).ToList();
        var poolTests = poolResult.Suites.SelectMany(s => s.Tests).ToList();
        Assert.Equal(seqTests.Count, poolTests.Count);

        // All tests should pass in both
        Assert.All(seqTests,  t => Assert.Equal(TestStatus.Passed, t.Status));
        Assert.All(poolTests, t => Assert.Equal(TestStatus.Passed, t.Status));
    }

    // ── Error isolation test ──────────────────────────────────────────────────

    [Fact]
    public async Task RunWithPool_CrashedProject_DoesNotPreventHealthyProjectsFromCompleting()
    {
        // A "crash" in the pool is when a project's process fails or throws.
        // We simulate this via the pool's fake delegate in unit tests.
        // Here we verify using real projects: one valid, one pointing to a non-existent csproj.
        var parser = new TrxResultParser();
        using var pool = new TestProcessPool(2, 50, parser);
        var sut = new TestRunnerService(parser, pool);

        // nonexistent.csproj will cause dotnet test to fail with no TRX output
        var nonExistentProject = Path.Combine(_root, "NonExistent", "NonExistent.csproj");
        var projectPaths = new[] { nonExistentProject, _alphaTestsCsproj }.ToList();

        var completedProjects = new System.Collections.Concurrent.ConcurrentBag<ProjectTestResult>();

        var result = await sut.RunTestsAsync(
            solutionPath: _root,
            testProjectPaths: projectPaths,
            filter: null,
            collectCoverage: false,
            onProgress: null,
            onProjectCompleted: r => completedProjects.Add(r),
            CancellationToken.None);

        // Alpha.Tests should still produce results
        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();
        Assert.True(allTests.Count > 0, "Healthy project should have produced test results.");

        // Both callbacks should have fired
        Assert.Equal(2, completedProjects.Count);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task WriteTestProject(string csprojPath, string dir, string namespaceName, int passCount)
    {
        await File.WriteAllTextAsync(csprojPath, """
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

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("using Xunit;");
        sb.AppendLine($"namespace {namespaceName}.Tests;");
        sb.AppendLine("public class Tests {");
        for (var i = 1; i <= passCount; i++)
            sb.AppendLine($"    [Fact] public void Passes{i}() => Assert.True(true);");
        sb.AppendLine("}");

        await File.WriteAllTextAsync(Path.Combine(dir, "Tests.cs"), sb.ToString());
    }

    private static async Task RunDotnetAsync(string args, string workDir)
    {
        using var p = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo("dotnet", args)
            {
                WorkingDirectory        = workDir,
                RedirectStandardOutput  = true,
                RedirectStandardError   = true,
                UseShellExecute         = false,
                CreateNoWindow          = true,
            }
        };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
    }
}
