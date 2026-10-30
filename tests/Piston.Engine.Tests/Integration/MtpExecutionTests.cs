// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
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
/// CI environment), existing tests return early; the coverage-attribution tests report a skip.
/// </remarks>
public sealed class MtpExecutionTests : IAsyncLifetime
{
    private string _root = string.Empty;
    private string _testCsproj = string.Empty;
    private bool _restored;
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
              <ItemGroup>
                <ProjectReference Include="Lib/Lib.csproj" />
                <Compile Remove="Lib/**/*.cs" />
              </ItemGroup>
            </Project>
            """);

        var library = Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName;
        await File.WriteAllTextAsync(Path.Combine(library, "Lib.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(library, "CoveredCode.cs"), """
            namespace MtpE2E.Library;

            public static class CoveredCode
            {
                public static int Passing()
                {
                    return 11;
                }

                public static int Failing()
                {
                    return 22;
                }
            }
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
                public async System.Threading.Tasks.Task PassingTest()
                {
                    Xunit.TestContext.Current.TestOutputHelper?.WriteLine("hello from PassingTest");
                    Xunit.Assert.Equal(11, MtpE2E.Library.CoveredCode.Passing());

                    var gate = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.AppContext.BaseDirectory, "../../../progress-gate"));
                    while (System.IO.File.Exists(gate))
                    {
                        await System.Threading.Tasks.Task.Delay(50, Xunit.TestContext.Current.CancellationToken);
                    }

                    // Server mode and dotnet test must both start the app in the solution directory, so
                    // solution-relative paths resolve identically on either path.
                    if (!System.IO.File.Exists(System.IO.Path.Combine(LaunchDirectory.Value, "global.json")))
                    {
                        throw new System.InvalidOperationException($"Started in {LaunchDirectory.Value}, not the solution directory.");
                    }
                }
            }

            public class FailingTests
            {
                [Xunit.Fact]
                public void FailingTest()
                {
                    Xunit.Assert.Equal(22, MtpE2E.Library.CoveredCode.Failing());
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

        _restored = true;
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
    [InlineData(true, "MTP server")]
    [InlineData(false, "MTP dotnet test")]
    public async Task RunAsync_WithCoverage_WritesCoberturaReports(bool serverMode, string runner)
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
            Assert.Equal(runner, Assert.Single(result.Suites).ExecutionLog!.Runner);
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
    public async Task SequentialFilteredCoverage_ReusesDiscoveryAndKeepsReportsSeparate()
    {
        Assert.SkipUnless(_restored, "MTP fixture packages could not be restored.");
        Assert.True(_available, "The MTP fixture must restore and build.");
        var cache = new MtpDiscoveryCache();
        var strategy = new MtpTestExecutionStrategy(_ => AppPath, _root, discoveryCache: cache);
        const string passing = "FullyQualifiedName=MtpE2E.SampleTests.PassingTest";
        const string failing = "FullyQualifiedName=MtpE2E.FailingTests.FailingTest";
        var results = new List<ProjectTestResult>();
        try
        {
            var passResult = await strategy.ExecuteAsync(
                new ProjectTestRequest(_testCsproj, passing, true) { ReuseDiscovery = true }, null, _ct);
            results.Add(passResult);
            var failResult = await strategy.ExecuteAsync(
                new ProjectTestRequest(_testCsproj, failing, true) { ReuseDiscovery = true }, null, _ct);
            results.Add(failResult);

            var passed = Assert.Single(passResult.Suites.SelectMany(suite => suite.Tests));
            Assert.True(passed.Status == TestStatus.Passed, $"{passed.Status}: {passed.ErrorMessage}");
            Assert.Equal(TestStatus.Failed, Assert.Single(failResult.Suites.SelectMany(suite => suite.Tests)).Status);
            Assert.All(new[] { passResult, failResult }, result =>
            {
                Assert.Null(result.RunnerError);
                Assert.False(result.Crashed);
                Assert.Equal("MTP server", result.Log!.Runner);
                _ = Assert.Single(result.CoverageReportPaths);
            });
            Assert.Contains(failResult.Log!.Entries, entry => entry.Message.Contains("(cached discovery)", StringComparison.Ordinal));

            var source = Path.Combine(_root, "Lib", "CoveredCode.cs");
            var lines = await File.ReadAllLinesAsync(source, _ct);
            var passLine = Array.FindIndex(lines, line => line.Contains("return 11;", StringComparison.Ordinal)) + 1;
            var failLine = Array.FindIndex(lines, line => line.Contains("return 22;", StringComparison.Ordinal)) + 1;
            Assert.True(passLine > 0 && failLine > 0);

            var passHits = CoverageHits(passResult, source);
            var failHits = CoverageHits(failResult, source);
            Assert.Contains(passLine, passHits);
            Assert.DoesNotContain(failLine, passHits);
            Assert.Contains(failLine, failHits);
            Assert.DoesNotContain(passLine, failHits);

            var foreground = await strategy.ExecuteAsync(new ProjectTestRequest(_testCsproj, passing, false), null, _ct);
            Assert.DoesNotContain(foreground.Log!.Entries, entry => entry.Message.Contains("(cached discovery)", StringComparison.Ordinal));
            var cachedRun = await strategy.ExecuteAsync(
                new ProjectTestRequest(_testCsproj, passing, false) { ReuseDiscovery = true }, null, _ct);
            Assert.Contains(cachedRun.Log!.Entries, entry => entry.Message.Contains("(cached discovery)", StringComparison.Ordinal));
            var build = await new BuildService(warmHosts: null, discoveryCache: cache).BuildAsync(_testCsproj, _ct);
            Assert.True(build.Status == BuildStatus.Succeeded, string.Join(Environment.NewLine, build.Errors));
            var afterBuild = await strategy.ExecuteAsync(
                new ProjectTestRequest(_testCsproj, passing, false) { ReuseDiscovery = true }, null, _ct);
            Assert.DoesNotContain(afterBuild.Log!.Entries, entry => entry.Message.Contains("(cached discovery)", StringComparison.Ordinal));
            Assert.Equal(TestStatus.Passed, Assert.Single(afterBuild.Suites.SelectMany(suite => suite.Tests)).Status);
        }
        finally
        {
            foreach (var dir in results.SelectMany(result => result.CoverageReportPaths)
                .Select(PistonOrchestrator.CoverageResultsDirectory).OfType<string>().Distinct())
            {
                try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task ParallelCoverageFromDifferentOutputs_RecordsOnlyEachProjectsHits()
    {
        Assert.SkipUnless(_restored, "MTP fixture packages could not be restored.");
        Assert.True(_available, "The MTP fixture must restore and build.");
        var otherRoot = Directory.CreateTempSubdirectory("piston-mtp-other-").FullName;
        var otherProject = Path.Combine(otherRoot, "OtherTests.csproj");
        var otherApp = Path.Combine(otherRoot, "bin", "Debug", "net10.0", "OtherTests.dll");
        var gate = Path.Combine(_root, "progress-gate");
        var tasks = new List<Task<ProjectTestResult>>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        try
        {
            var tests = System.Security.SecurityElement.Escape(Path.Combine(_root, "Tests.cs"));
            var library = System.Security.SecurityElement.Escape(Path.Combine(_root, "Lib", "Lib.csproj"));
            await File.WriteAllTextAsync(otherProject, $$"""
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
                    <ProjectReference Include="{{library}}" />
                    <Compile Include="{{tests}}" Link="Tests.cs" />
                  </ItemGroup>
                </Project>
                """, _ct);
            Assert.True(await TryRunDotnetAsync("restore", otherRoot));
            Assert.True(await TryRunDotnetAsync("build --configuration Debug --no-restore", otherRoot));
            await File.WriteAllTextAsync(gate, "", _ct);

            var strategy = new MtpTestExecutionStrategy(
                project => project == otherProject ? otherApp : AppPath, _root);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var passTask = strategy.ExecuteAsync(
                new ProjectTestRequest(_testCsproj, "FullyQualifiedName=MtpE2E.SampleTests.PassingTest", true),
                suites =>
                {
                    if (suites.SelectMany(suite => suite.Tests).Any(test => test.Status == TestStatus.Running))
                    {
                        _ = started.TrySetResult();
                    }
                },
                stop.Token);
            tasks.Add(passTask);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(60), _ct);

            var failTask = strategy.ExecuteAsync(
                new ProjectTestRequest(otherProject, "FullyQualifiedName=MtpE2E.FailingTests.FailingTest", true),
                null,
                stop.Token);
            tasks.Add(failTask);
            var failResult = await failTask.WaitAsync(TimeSpan.FromSeconds(60), _ct);
            Assert.False(passTask.IsCompleted);
            File.Delete(gate);
            var passResult = await passTask.WaitAsync(TimeSpan.FromSeconds(60), _ct);

            var passed = Assert.Single(passResult.Suites.SelectMany(suite => suite.Tests));
            Assert.True(passed.Status == TestStatus.Passed, $"{passed.Status}: {passed.ErrorMessage}");
            Assert.Equal(TestStatus.Failed, Assert.Single(failResult.Suites.SelectMany(suite => suite.Tests)).Status);
            Assert.All(new[] { passResult, failResult }, result =>
            {
                Assert.Null(result.RunnerError);
                Assert.False(result.Crashed);
                _ = Assert.Single(result.CoverageReportPaths);
            });
            Assert.NotEqual(passResult.CoverageReportPaths[0], failResult.CoverageReportPaths[0]);

            var source = Path.Combine(_root, "Lib", "CoveredCode.cs");
            var lines = await File.ReadAllLinesAsync(source, _ct);
            var passLine = Array.FindIndex(lines, line => line.Contains("return 11;", StringComparison.Ordinal)) + 1;
            var failLine = Array.FindIndex(lines, line => line.Contains("return 22;", StringComparison.Ordinal)) + 1;
            Assert.True(passLine > 0 && failLine > 0);
            var passHits = CoverageHits(passResult, source);
            var failHits = CoverageHits(failResult, source);
            Assert.Contains(passLine, passHits);
            Assert.DoesNotContain(failLine, passHits);
            Assert.Contains(failLine, failHits);
            Assert.DoesNotContain(passLine, failHits);
        }
        finally
        {
            File.Delete(gate);
            await stop.CancelAsync();
            try
            {
                _ = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60), _ct);
            }
            finally
            {
                foreach (var task in tasks.Where(task => task.IsCompletedSuccessfully))
                {
                    var result = await task;
                    foreach (var dir in result.CoverageReportPaths
                        .Select(PistonOrchestrator.CoverageResultsDirectory).OfType<string>().Distinct())
                    {
                        try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ }
                    }
                }

                try { Directory.Delete(otherRoot, recursive: true); } catch { /* ignore */ }
            }
        }
    }

    [Fact]
    public async Task RunAsync_WithCoverage_ServerModeStreamsProgress()
    {
        if (!_available)
        {
            return; // packages unavailable — skip gracefully
        }

        var snapshots = new List<IReadOnlyList<TestResult>>();
        var partialResults = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = Path.Combine(_root, "progress-gate");
        await File.WriteAllTextAsync(gate, "", _ct);
        using var stopRun = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        var runTask = MtpTestProcessRunner.RunAsync(
            projectPath: _testCsproj,
            appPath: AppPath,
            solutionDirectory: _root,
            filter: null,
            onProgress: suites =>
            {
                lock (snapshots)
                {
                    snapshots.Add([.. suites.SelectMany(s => s.Tests)]);
                    if (snapshots[^1].Any(t => t.Status == TestStatus.Running)
                        || snapshots[^1] is { Count: 1 } partial && partial[0].Status == TestStatus.Failed)
                    {
                        _ = partialResults.TrySetResult();
                    }
                }
            },
            ct: stopRun.Token,
            MtpCoverage.Collect);
        ProjectTestResult? result = null;
        try
        {
            await partialResults.Task.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            Assert.False(runTask.IsCompleted);
            File.Delete(gate);
            result = await runTask.WaitAsync(TimeSpan.FromSeconds(30), _ct);
            Assert.Equal("MTP server", Assert.Single(result.Suites).ExecutionLog!.Runner);
            Assert.Contains(snapshots, s => s.Any(t => t.Status == TestStatus.Running)
                || s.Count == 1 && s[0].Status == TestStatus.Failed);
            Assert.All(snapshots[^1], t => Assert.NotEqual(TestStatus.Running, t.Status));
            _ = Assert.Single(result.CoverageReportPaths);
        }
        finally
        {
            File.Delete(gate);
            await stopRun.CancelAsync();
            result ??= await runTask;
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

    private static HashSet<int> CoverageHits(ProjectTestResult result, string source) => new CoberturaParser()
        .Parse(Assert.Single(result.CoverageReportPaths)).Files
        .Where(file => string.Equals(file.FilePath, source, StringComparison.OrdinalIgnoreCase))
        .SelectMany(file => file.Lines)
        .Where(line => line.HitCount > 0)
        .Select(line => line.LineNumber)
        .ToHashSet();

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
