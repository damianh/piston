using System.Diagnostics;
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

            public class SampleTests
            {
                [Xunit.Fact]
                public void PassingTest()
                {
                    // This test should pass.
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

    [Fact]
    public async Task RunAsync_ReturnsOnePassAndOneFail()
    {
        if (!_available) return; // packages unavailable — skip gracefully

        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      _testCsproj,
            solutionDirectory: _root,
            filter:           null,
            collectCoverage:  false,
            onProgress:       null,
            ct:               CancellationToken.None);

        Assert.NotNull(result);
        Assert.False(result.Crashed);

        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();

        var passing = allTests.Where(t => t.Status == TestStatus.Passed).ToList();
        var failing = allTests.Where(t => t.Status == TestStatus.Failed).ToList();

        Assert.Single(passing);
        Assert.Single(failing);

        Assert.Contains(passing, t => t.FullyQualifiedName.Contains("PassingTest", StringComparison.Ordinal));
        Assert.Contains(failing, t => t.FullyQualifiedName.Contains("FailingTest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_FailingTest_HasNonEmptyErrorMessage()
    {
        if (!_available) return; // packages unavailable — skip gracefully

        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      _testCsproj,
            solutionDirectory: _root,
            filter:           null,
            collectCoverage:  false,
            onProgress:       null,
            ct:               CancellationToken.None);

        var failing = result.Suites
            .SelectMany(s => s.Tests)
            .Single(t => t.Status == TestStatus.Failed);

        Assert.NotNull(failing.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(failing.ErrorMessage));
    }

    [Fact]
    public async Task RunAsync_AllTests_HaveNonNegativeDuration()
    {
        if (!_available) return; // packages unavailable — skip gracefully

        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      _testCsproj,
            solutionDirectory: _root,
            filter:           null,
            collectCoverage:  false,
            onProgress:       null,
            ct:               CancellationToken.None);

        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();
        Assert.NotEmpty(allTests);

        foreach (var test in allTests)
            Assert.True(test.Duration >= TimeSpan.Zero,
                $"Expected non-negative duration for {test.FullyQualifiedName}");
    }

    [Fact]
    public async Task RunAsync_WithFilter_RunsOnlyMatchingTest()
    {
        if (!_available) return; // packages unavailable — skip gracefully

        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      _testCsproj,
            solutionDirectory: _root,
            filter:           "PassingTest",
            collectCoverage:  false,
            onProgress:       null,
            ct:               CancellationToken.None);

        var allTests = result.Suites.SelectMany(s => s.Tests).ToList();

        // Only the passing test should be in results when filtered
        Assert.All(allTests,
            t => Assert.Contains("PassingTest", t.FullyQualifiedName, StringComparison.Ordinal));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<bool> TryRunDotnetAsync(string args, string workDir)
    {
        try
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo("dotnet", args)
                {
                    WorkingDirectory       = workDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
