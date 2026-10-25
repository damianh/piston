// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Orchestration;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Integration;

/// <summary>
/// End-to-end integration test for MTP v2 test execution via
/// <see cref="MtpTestProcessRunner"/>. Creates a minimal xUnit v3 project in a temp
/// directory with a <c>global.json</c> that enables MTP mode for <c>dotnet test</c>,
/// builds it, and verifies that the runner returns correctly parsed results
/// for both passing and failing tests.
/// </summary>
/// <remarks>
/// The test project uses explicit package versions and lives outside the repo root so
/// it does not inherit the solution's <c>Directory.Packages.props</c> (which does not
/// include xUnit v3 or MTP packages). If packages cannot be restored (e.g. offline
/// CI environment), the tests return early without asserting (pass vacuously).
/// </remarks>
public sealed class MtpExecutionTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _testCsproj = string.Empty;
    private bool _available;
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _root = Directory.CreateTempSubdirectory("piston-mtp-e2e-").FullName;

        // global.json to enable MTP mode for dotnet test
        await File.WriteAllTextAsync(Path.Combine(_root, "global.json"), """
            {
              "test": {
                "runner": "Microsoft.Testing.Platform"
              }
            }
            """);

        _testCsproj = Path.Combine(_root, "MtpE2E.csproj");
        await File.WriteAllTextAsync(_testCsproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <OutputType>Exe</OutputType>
                <IsTestingPlatformApplication>true</IsTestingPlatformApplication>
                <NoWarn>$(NoWarn);NU1507</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="xunit.v3.mtp-v2" Version="3.2.2" />
                <PackageReference Include="Microsoft.Testing.Extensions.CodeCoverage" Version="18.12.0" />
              </ItemGroup>
            </Project>
            """);

        await File.WriteAllTextAsync(Path.Combine(_root, "Tests.cs"), """
            namespace MtpE2E;

            internal static class LaunchDirectory
            {
                internal static string Value = "";

                // Runs before the test framework starts (and may change the current directory).
                [System.Runtime.CompilerServices.ModuleInitializer]
                internal static void Capture() => Value = System.Environment.CurrentDirectory;
            }

            public class SampleTests
            {
                [Xunit.Fact]
                public void PassingTest()
                {
                    Xunit.TestContext.Current.TestOutputHelper?.WriteLine("hello from PassingTest");

                    // Server mode and dotnet test must both start the app in the solution directory, so
                    // solution-relative paths resolve identically on either path.
                    if (!System.IO.File.Exists(System.IO.Path.Combine(LaunchDirectory.Value, "global.json")))
                    {
                        throw new System.InvalidOperationException($"Started in {LaunchDirectory.Value}, not the solution directory.");
                    }
                }

                [Xunit.Fact]
                public void FailingTest()
                {
                    throw new System.InvalidOperationException("expected failure");
                }
            }
            """);

        // Restore packages; skip test gracefully if packages are unavailable.
        if (!await TryRunDotnetAsync("restore", _root))
        {
            _available = false;
            return;
        }

        if (!await TryRunDotnetAsync("build --configuration Debug --no-restore", _root))
        {
            _available = false;
            return;
        }

        _available = true;
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return ValueTask.CompletedTask;
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CoordinatedProject_DoesNotRetainAHost_AfterDiscoveryOrExecution()
    {
        Assert.True(_available, "The MTP fixture must restore and build.");
        await using var warmHosts = new MtpWarmHostPool();
        var app = Path.Combine(_root, "bin", "Debug", "net10.0", "MtpE2E.dll");
        var strategy = new MtpTestExecutionStrategy(
            _ => app, _root, warmHosts: warmHosts, requiresResources: _ => true);
        const string filter = "FullyQualifiedName=MtpE2E.SampleTests.PassingTest";

        var discovered = await strategy.DiscoverAsync(_testCsproj, filter, null, _ct);
        _ = Assert.Single(discovered);
        Assert.Equal(0, warmHosts.Count);

        var result = await strategy.ExecuteAsync(new ProjectTestRequest(_testCsproj, filter, false), null, _ct);

        Assert.Null(result.RunnerError);
        Assert.Equal(TestStatus.Passed, Assert.Single(result.Suites.SelectMany(s => s.Tests)).Status);
        Assert.Equal(0, warmHosts.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_ReturnsOnePassAndOneFail(bool serverMode)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode, filter: null);

        Assert.NotNull(result);
        Assert.False(result.Crashed);

        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();

        var passing = allTests.Where(t => t.Status == TestStatus.Passed).ToList();
        var failing = allTests.Where(t => t.Status == TestStatus.Failed).ToList();

        _ = Assert.Single(passing);
        _ = Assert.Single(failing);

        Assert.Contains(passing, t => t.FullyQualifiedName.Contains("PassingTest", StringComparison.Ordinal));
        Assert.Contains(failing, t => t.FullyQualifiedName.Contains("FailingTest", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_FailingTest_HasNonEmptyErrorMessage(bool serverMode)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode, filter: null);

        var failing = result.Suites
            .SelectMany(s => s.Tests)
            .Single(t => t.Status == TestStatus.Failed);

        Assert.NotNull(failing.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(failing.ErrorMessage));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_AllTests_HaveNonNegativeDuration(bool serverMode)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode, filter: null);

        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();
        Assert.NotEmpty(allTests);

        foreach (var test in allTests)
        {
            Assert.True(test.Duration >= TimeSpan.Zero,
                $"Expected non-negative duration for {test.FullyQualifiedName}");
        }
    }

    [Fact]
    public async Task RunAsync_ServerModeWithFilter_RunsOnlyMatchingTest()
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode: true, filter: "PassingTest");

        var test = Assert.Single(result.Suites.SelectMany(s => s.Tests));
        Assert.Contains("PassingTest", test.FullyQualifiedName, StringComparison.Ordinal);
        Assert.Equal(TestStatus.Passed, test.Status);
    }

    [Fact]
    public async Task RunAsync_ServerModeFilterMatchingNothing_RunsNoTests()
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode: true, filter: "FullyQualifiedName=Nope");

        Assert.Empty(result.Suites.SelectMany(s => s.Tests));
        Assert.Null(result.RunnerError);
    }

    [Fact]
    public async Task RunAsync_ServerMode_StreamsProgressAndEndsWithNoRunningTests()
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var snapshots = new List<IReadOnlyList<TestResult>>();
        var result = await MtpTestProcessRunner.RunAsync(
            projectPath: _testCsproj,
            appPath: AppPath,
            solutionDirectory: _root,
            filter: null,
            onProgress: suites =>
            {
                lock (snapshots)
                {
                    snapshots.Add([.. suites.SelectMany(s => s.Tests)]);
                }
            },
            ct: CancellationToken.None);

        Assert.Equal(2, result.Suites.SelectMany(s => s.Tests).Count());
        Assert.NotEmpty(snapshots);
        Assert.All(snapshots[^1], t => Assert.NotEqual(TestStatus.Running, t.Status));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiscoverAsync_ListsTheSameNamesTheRunReports(bool serverMode)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        Assert.True(!serverMode || File.Exists(AppPath), $"Test app not found: {AppPath}");
        var partials = new List<int>();
        var discovered = await MtpTestProcessRunner.DiscoverAsync(
            _testCsproj, serverMode ? AppPath : null, _root, filter: null, tests => partials.Add(tests.Count), CancellationToken.None);
        var result = await RunAsync(serverMode, filter: null);

        Assert.Equal(2, discovered.Count);
        Assert.NotEmpty(partials);
        Assert.All(discovered, t => Assert.Equal(TestStatus.NotRun, t.Status));
        Assert.Equal(
            result.Suites.SelectMany(s => s.Tests).Select(t => t.FullyQualifiedName).Order(StringComparer.Ordinal),
            discovered.Select(t => t.FullyQualifiedName).Order(StringComparer.Ordinal));

        var suite = Assert.Single(result.Suites);
        Assert.Equal("MtpE2E", suite.Name);
    }

    [Fact]
    public async Task DiscoverAsync_ServerModeWithFilter_ListsOnlyMatchingTests()
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var discovered = await MtpTestProcessRunner.DiscoverAsync(
            _testCsproj, AppPath, _root, filter: "Name=FailingTest", onPartial: null, CancellationToken.None);

        var test = Assert.Single(discovered);
        Assert.Contains("FailingTest", test.FullyQualifiedName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_CapturesTestOutput(bool serverMode)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode, filter: null);

        var passing = result.Suites.SelectMany(s => s.Tests).Single(t => t.Status == TestStatus.Passed);
        Assert.Contains("hello from PassingTest", passing.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "MTP server")]
    [InlineData(false, "MTP dotnet test")]
    public async Task RunAsync_AttachesTheRunLogToTheSuite(bool serverMode, string runner)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode, filter: null);

        var log = Assert.Single(result.Suites).ExecutionLog;
        Assert.NotNull(log);
        Assert.Equal(runner, log.Runner);
        Assert.Equal(_testCsproj, log.ProjectPath);
        _ = Assert.NotNull(log.CompletedAt);
        Assert.Contains(log.Entries, e => e.Message.Contains(_testCsproj, StringComparison.Ordinal));
        if (!serverMode)
        {
            _ = Assert.NotNull(log.ExitCode);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_WithCoverage_WritesCoberturaReports(bool serverMode)
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        Assert.True(MtpTestExecutionStrategy.HasCoverageExtension(AppPath));
        var result = await RunAsync(serverMode, filter: null, MtpCoverage.Collect);
        try
        {
            Assert.Equal(2, result.Suites.SelectMany(s => s.Tests).Count());
            Assert.Equal("MTP dotnet test", Assert.Single(result.Suites).ExecutionLog!.Runner);
            var report = Assert.Single(result.CoverageReportPaths);
            Assert.Contains("<coverage", await File.ReadAllTextAsync(report, _ct), StringComparison.Ordinal);
            Assert.NotNull(PistonOrchestrator.CoverageResultsDirectory(report));
        }
        finally
        {
            foreach (var dir in result.CoverageReportPaths.Select(PistonOrchestrator.CoverageResultsDirectory).OfType<string>().Distinct())
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task RunAsync_CoverageExtensionMissing_RunsWithoutCoverageAndSaysWhy()
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var result = await RunAsync(serverMode: true, filter: null, MtpCoverage.ExtensionMissing);

        Assert.Empty(result.CoverageReportPaths);
        Assert.Equal(2, result.Suites.SelectMany(s => s.Tests).Count());
        Assert.Contains(Assert.Single(result.Suites).ExecutionLog!.Entries,
            e => e.Message.Contains("Coverage not collected", StringComparison.Ordinal));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string AppPath => Path.Combine(_root, "bin", "Debug", "net10.0", "MtpE2E.dll");

    // Server mode needs the built test app; without it the runner falls back to dotnet test.
    private Task<ProjectTestResult> RunAsync(bool serverMode, string? filter, MtpCoverage coverage = MtpCoverage.Off)
    {
        Assert.True(!serverMode || File.Exists(AppPath), $"Test app not found: {AppPath}");
        return MtpTestProcessRunner.RunAsync(
            projectPath: _testCsproj,
            appPath: serverMode ? AppPath : null,
            solutionDirectory: _root,
            filter: filter,
            onProgress: null,
            ct: CancellationToken.None,
            coverage);
    }

    private static async Task<bool> TryRunDotnetAsync(string args, string workDir)
    {
        var result = await TestProcess.RunDotnetResultAsync(args, workDir);
        return result.ExitCode == 0;
    }
}
