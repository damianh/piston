// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
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

    public async Task InitializeAsync()
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
                <PackageReference Include="xunit.v3" Version="3.2.2" />
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

    public Task DisposeAsync()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
        return Task.CompletedTask;
    }

    // ── Tests ──────────────────────────────────────────────────────────────────

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

    // ── Helpers ────────────────────────────────────────────────────────────────

    private string AppPath => Path.Combine(_root, "bin", "Debug", "net10.0", "MtpE2E.dll");

    // Server mode needs the built test app; without it the runner falls back to dotnet test.
    private Task<ProjectTestResult> RunAsync(bool serverMode, string? filter)
    {
        Assert.True(!serverMode || File.Exists(AppPath), $"Test app not found: {AppPath}");
        return MtpTestProcessRunner.RunAsync(
            projectPath: _testCsproj,
            appPath: serverMode ? AppPath : null,
            solutionDirectory: _root,
            filter: filter,
            onProgress: null,
            ct: CancellationToken.None);
    }

    private static async Task<bool> TryRunDotnetAsync(string args, string workDir)
    {
        var result = await TestProcess.RunDotnetResultAsync(args, workDir);
        return result.ExitCode == 0;
    }
}
