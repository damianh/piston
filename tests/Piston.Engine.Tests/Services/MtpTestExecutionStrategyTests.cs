// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpTestExecutionStrategyTests
{
    [Fact]
    public async Task CoordinatedProjects_DoNotRetainWarmHosts_ForExecutionOrDiscovery()
    {
        await using var pool = new MtpWarmHostPool();
        var strategy = new MtpTestExecutionStrategy(
            _ => "/path/to/tests.dll",
            Path.GetTempPath(),
            warmHosts: pool,
            requiresResources: project => project == "integration.csproj");

        Assert.Null(strategy.WarmHostsFor("integration.csproj"));
        Assert.Same(pool, strategy.WarmHostsFor("unit.csproj"));
    }

    [Fact]
    public void CanExecute_WhenOutputPathAvailable_ReturnsTrue()
    {
        var strategy = new MtpTestExecutionStrategy(
            _ => "/path/to/tests.dll",
            Path.GetTempPath());

        Assert.True(strategy.CanExecute("my.csproj"));
    }

    [Fact]
    public void CanExecute_WhenNoOutputPath_ReturnsFalse()
    {
        var strategy = new MtpTestExecutionStrategy(
            _ => null,
            Path.GetTempPath());

        Assert.False(strategy.CanExecute("my.csproj"));
    }

    [Fact]
    public void CanExecute_DelegateSeeProjectPath()
    {
        string? seenPath = null;
        var strategy = new MtpTestExecutionStrategy(
            p => { seenPath = p; return null; },
            Path.GetTempPath());

        _ = strategy.CanExecute("specific.csproj");

        Assert.Equal("specific.csproj", seenPath);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ReturnsEmptyResult()
    {
        var strategy = new MtpTestExecutionStrategy(
            _ => Path.Combine(Path.GetTempPath(), "nonexistent.dll"),
            Path.GetTempPath());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("my.csproj", null, false);
        var result = await strategy.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNonMtpProject_StillRunsViaRunner()
    {
        // Even when CanExecute would return false (null output path),
        // ExecuteAsync is still callable — the CompositeStrategy is responsible
        // for gating. This just verifies no crash.
        var strategy = new MtpTestExecutionStrategy(
            _ => Path.Combine(Path.GetTempPath(), "nonexistent.dll"),
            Path.GetTempPath());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("my.csproj", null, false);
        var result = await strategy.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
    }

    [Fact]
    public void ServerAppPath_MultiTargetedProject_UsesDotnetTestForDiscoveryAndExecution()
    {
        var strategy = new MtpTestExecutionStrategy(
            _ => "/path/to/tests.dll",
            Path.GetTempPath(),
            p => p == "multi.csproj");

        // Only one framework's output is known, so server mode would skip the others.
        Assert.True(strategy.CanExecute("multi.csproj"));
        Assert.Null(strategy.ServerAppPath("multi.csproj"));
        Assert.Equal("/path/to/tests.dll", strategy.ServerAppPath("single.csproj"));
    }

    [Fact]
    public void HasCoverageExtension_LooksForTheExtensionNextToTheTestApp()
    {
        var dir = Directory.CreateTempSubdirectory("piston-cov-ext-").FullName;
        try
        {
            var app = Path.Combine(dir, "Tests.dll");
            Assert.False(MtpTestExecutionStrategy.HasCoverageExtension(app));

            File.WriteAllText(Path.Combine(dir, MtpTestExecutionStrategy.CoverageExtensionAssembly), "");

            Assert.True(MtpTestExecutionStrategy.HasCoverageExtension(app));
            Assert.False(MtpTestExecutionStrategy.HasCoverageExtension(null));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("all")]
    [InlineData("first-missing")]
    [InlineData("second-missing")]
    [InlineData("third-missing")]
    public void CoverageFor_MultiTargetRequiresTheExtensionAtEveryOutput(string scenario)
    {
        var root = Directory.CreateTempSubdirectory("piston-mtp-targets-").FullName;
        try
        {
            var first = Target(root, "net8.0", extension: scenario != "first-missing");
            var second = Target(root, "net9.0", extension: scenario != "second-missing");
            var third = Target(root, "net10.0", extension: scenario != "third-missing");
            var strategy = new MtpTestExecutionStrategy(
                _ => first, root, isMultiTargeted: _ => true,
                getMtpTargetOutputs: _ => [("net8.0", first), ("net9.0", second), ("net10.0", third)]);

            var decision = strategy.CoverageFor(new ProjectTestRequest("Multi.csproj", null, true));

            Assert.Equal(scenario == "all" ? MtpCoverage.Collect : MtpCoverage.MultiTargetExtensionMissing,
                decision.Coverage);
            Assert.Equal(scenario == "all" ? 3 : 0, decision.ExpectedReports);
            Assert.Equal(["net8.0", "net9.0", "net10.0"],
                decision.TargetOutputs.Select(target => target.Framework));
            Assert.Null(strategy.ServerAppPath("Multi.csproj"));
            Assert.Equal(MtpCoverage.Off,
                strategy.CoverageFor(new ProjectTestRequest("Multi.csproj", null, false)).Coverage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("no-provider")]
    [InlineData("missing-framework")]
    [InlineData("duplicate-framework")]
    [InlineData("empty-framework")]
    [InlineData("duplicate-output")]
    public void CoverageFor_MultiTargetRequiresDistinctKnownFrameworkNames(string scenario)
    {
        var root = Directory.CreateTempSubdirectory("piston-mtp-frameworks-").FullName;
        try
        {
            var first = Target(root, "net9.0", extension: true);
            var second = Target(root, "net10.0", extension: true);
            Func<string, IReadOnlyList<(string Framework, string OutputPath)>>? targets = scenario switch
            {
                "missing-framework" => _ => [("net9.0", first)],
                "duplicate-framework" => _ => [("net9.0", first), ("NET9.0", second)],
                "empty-framework" => _ => [("net9.0", first), (" ", second)],
                "duplicate-output" => _ => [("net9.0", first), ("net10.0", first)],
                _ => null,
            };
            var strategy = new MtpTestExecutionStrategy(
                _ => first, root, isMultiTargeted: _ => true, getMtpTargetOutputs: targets);

            var decision = strategy.CoverageFor(new ProjectTestRequest("Multi.csproj", null, true));

            Assert.Equal(MtpCoverage.OutputUnverified, decision.Coverage);
            Assert.Equal(0, decision.ExpectedReports);
            Assert.Empty(decision.TargetOutputs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("no-provider")]
    [InlineData("one-output")]
    [InlineData("missing-output")]
    public void CoverageFor_MultiTargetWithUnverifiableOutputsDoesNotEnableCollection(string scenario)
    {
        var root = Directory.CreateTempSubdirectory("piston-mtp-unverified-").FullName;
        try
        {
            var first = Target(root, "net8.0", extension: true);
            var missing = Path.Combine(root, "net10.0", "Tests.dll");
            Func<string, IReadOnlyList<(string Framework, string OutputPath)>>? targets = scenario switch
            {
                "one-output" => _ => [("net8.0", first)],
                "missing-output" => _ => [("net8.0", first), ("net10.0", missing)],
                _ => null,
            };
            var strategy = new MtpTestExecutionStrategy(
                _ => first, root, isMultiTargeted: _ => true, getMtpTargetOutputs: targets);

            var decision = strategy.CoverageFor(new ProjectTestRequest("Multi.csproj", null, true));

            Assert.Equal(MtpCoverage.OutputUnverified, decision.Coverage);
            Assert.Equal(0, decision.ExpectedReports);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Target(string root, string framework, bool extension)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, framework)).FullName;
        var app = Path.Combine(directory, "Tests.dll");
        File.WriteAllText(app, "");
        if (extension)
        {
            File.WriteAllText(Path.Combine(directory, MtpTestExecutionStrategy.CoverageExtensionAssembly), "");
        }

        return app;
    }
}
