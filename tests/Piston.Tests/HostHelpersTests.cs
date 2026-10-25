// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine;
using Piston.Hosting;
using Xunit;

namespace Piston.Tests;

public sealed class HostHelpersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-config-test-").FullName;

    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    [InlineData(".slnf")]
    public void ExplicitArgumentWinsOverConfiguredSolution(string extension)
    {
        WriteConfig("missing.slnx");
        var explicitPath = WriteSolution("Explicit" + extension);

        var result = HostHelpers.ResolveSolution(new FileInfo(explicitPath), _root);

        Assert.Equal(explicitPath, result.SolutionPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfiguredSolutionWinsOverDiscoveryAndKeepsItsConfig(bool absolute)
    {
        _ = WriteSolution("Other.slnx");
        _ = WriteSolution("Another.sln");
        var configuredPath = WriteSolution(Path.Combine("nested", "Configured.slnx"));
        WriteConfig(absolute ? configuredPath : Path.GetRelativePath(_root, configuredPath));
        File.WriteAllText(Path.Combine(_root, "nested", ".piston.json"), """{"pipeName":"wrong-config"}""");

        var result = HostHelpers.ResolveSolution(null, _root);

        Assert.Equal(configuredPath, result.SolutionPath);
        Assert.Equal("configured-pipe", result.Config.PipeName);
        Assert.True(result.Config.Stdio);
    }

    [Fact]
    public void ConfiguredSolutionDoesNotRequireSolutionInCurrentDirectory()
    {
        var path = WriteSolution(Path.Combine("nested", "Configured.slnx"));
        WriteConfig("nested/Configured.slnx");

        Assert.Equal(path, HostHelpers.ResolveSolutionPath(null, _root));
    }

    [Fact]
    public void MissingConfiguredSolutionReportsConfigAndPathInsteadOfDiscovering()
    {
        _ = WriteSolution("Discovered.slnx");
        WriteConfig("nested/Missing.slnx");

        var error = Assert.Throws<InvalidOperationException>(() => HostHelpers.ResolveSolutionPath(null, _root));

        Assert.Contains(Path.Combine(_root, ".piston.json"), error.Message);
        Assert.Contains(Path.Combine(_root, "nested", "Missing.slnx"), error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("Wrong.csproj")]
    public void InvalidConfiguredSolutionReportsConfig(string value)
    {
        _ = WriteSolution("Wrong.csproj");
        WriteConfig(value);

        var error = Assert.Throws<InvalidOperationException>(() => HostHelpers.ResolveSolutionPath(null, _root));

        Assert.Contains(".piston.json", error.Message);
    }

    [Fact]
    public void MalformedConfigReportsErrorRatherThanIgnoringSolution()
    {
        _ = WriteSolution("Discovered.slnx");
        File.WriteAllText(Path.Combine(_root, ".piston.json"), "{ malformed");

        var error = Assert.Throws<InvalidOperationException>(() => HostHelpers.ResolveSolutionPath(null, _root));

        Assert.Contains("Could not load configuration", error.Message);
        Assert.Contains(".piston.json", error.Message);
    }

    [Theory]
    [InlineData(".sln")]
    [InlineData(".slnx")]
    [InlineData(".slnf")]
    public void NoConfiguredSolutionFallsBackToUniqueDiscovery(string extension)
    {
        var path = WriteSolution("Discovered" + extension);
        File.WriteAllText(Path.Combine(_root, ".piston.json"), """{"debounceMs":500}""");

        var result = HostHelpers.ResolveSolution(null, _root);

        Assert.Equal(path, result.SolutionPath);
        Assert.Equal(500, result.Config.DebounceMs);
    }

    [Fact]
    public void NoConfigFallsBackToUniqueDiscovery()
    {
        var path = WriteSolution("Discovered.slnx");
        Assert.Equal(path, HostHelpers.ResolveSolutionPath(null, _root));
    }

    [Fact]
    public void DiscoveryStillRejectsMissingOrAmbiguousSolutions()
    {
        Assert.Contains("No .sln", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(null, _root)).Message);
        _ = WriteSolution("First.slnx");
        _ = WriteSolution("Second.sln");
        Assert.Contains("Multiple solution files", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(null, _root)).Message);
    }

    [Fact]
    public void InvalidExplicitArgumentDoesNotFallBackToConfig()
    {
        WriteConfig("Configured.slnx");
        _ = WriteSolution("Configured.slnx");
        Assert.Contains("Solution file not found", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(new FileInfo(Path.Combine(_root, "Missing.slnx")), _root)).Message);
        var wrongPath = WriteSolution("Wrong.csproj");
        Assert.Contains("Expected a .sln", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(new FileInfo(wrongPath), _root)).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveExecutionMemoryIsRejected(int memoryMb)
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { Execution = new() { MemoryMb = memoryMb } };

        var ex = Assert.Throws<InvalidOperationException>(() => HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config));

        Assert.Contains("execution.memoryMb", ex.Message);
    }

    [Fact]
    public void ExecutionMemoryIsPassedToTheEngine()
    {
        var configured = new Piston.Hosting.Configuration.PistonConfig { Execution = new() { MemoryMb = 2048 } };

        Assert.Equal(2048, HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, configured).ExecutionMemoryReservationMb);
        Assert.Null(HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, new()).ExecutionMemoryReservationMb);
    }

    [Theory]
    [InlineData(null, true, ImpactBackfillMode.Test)]
    [InlineData("test", true, ImpactBackfillMode.Test)]
    [InlineData("OFF", true, ImpactBackfillMode.Off)]
    [InlineData("class", false, ImpactBackfillMode.Off)]
    public void ImpactBackfillDefaultsToTestWhenCoverageIsEnabled(string? configured, bool coverage, ImpactBackfillMode expected)
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { ImpactBackfill = configured };

        Assert.Equal(expected, HostHelpers.BuildOptions("/w/App.slnx", 0, null, coverage, 0, config).ImpactBackfill);
    }

    [Theory]
    [InlineData("method")]
    [InlineData("2")]
    public void UnknownImpactBackfillIsRejected(string configured)
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { ImpactBackfill = configured };

        var ex = Assert.Throws<InvalidOperationException>(() => HostHelpers.BuildOptions("/w/App.slnx", 0, null, true, 0, config));

        Assert.Contains("impactBackfill", ex.Message);
    }

    [Fact]
    public void HangThresholdDefaultsTo60SecondsAndTestTimeoutIsOff()
    {
        var options = HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, new Piston.Hosting.Configuration.PistonConfig());

        Assert.Equal(TimeSpan.FromSeconds(60), options.HangThreshold);
        Assert.Null(options.TestTimeout);
    }

    [Fact]
    public void HangThresholdAndTestTimeoutAreRead()
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { HangThreshold = "off", TestTimeout = "5m" };

        var options = HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config);

        Assert.Null(options.HangThreshold);
        Assert.Equal(TimeSpan.FromMinutes(5), options.TestTimeout);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void HistoryIsOnUnlessConfiguredOff(bool? configured, bool expected)
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { History = configured };

        Assert.Equal(expected, HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config).HistoryEnabled);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData(0, 0)]
    [InlineData(3, 3)]
    public void RetryFailuresIsOffUnlessConfigured(int? configured, int expected)
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { RetryFailures = configured };

        Assert.Equal(expected, HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config).RetryFailedTests);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void RetryFailuresOutOfRangeIsRejected(int configured)
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { RetryFailures = configured };

        var ex = Assert.Throws<InvalidOperationException>(() => HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config));

        Assert.Contains("retryFailures", ex.Message);
    }

    [Fact]
    public void InvalidTestTimeoutIsRejected()
    {
        var config = new Piston.Hosting.Configuration.PistonConfig { TestTimeout = "soon" };

        var ex = Assert.Throws<InvalidOperationException>(() => HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config));

        Assert.Contains("testTimeout", ex.Message);
    }

    [Fact]
    public void TestResourcesAreLoadedAndNormalized()
    {
        File.WriteAllText(Path.Combine(_root, ".piston.json"), """
            {
              "testResources": [
                { "projects": " tests/**/*.IntegrationTests.csproj ", "exclusive": ["Port:5432", "container:pg"], "isolation": "None" },
                { "projects": "tests/Unit.Tests.csproj", "containers": false }
              ]
            }
            """);

        var config = HostHelpers.LoadConfig(_root);
        var rules = HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config).TestResourceRules;

        Assert.Equal(2, rules.Count);
        Assert.Equal("tests/**/*.IntegrationTests.csproj", rules[0].Projects);
        Assert.Equal(["port:5432", "container:pg"], rules[0].Exclusive);
        Assert.Equal(Piston.Engine.Execution.TestResourceIsolation.None, rules[0].Isolation);
        Assert.Null(rules[0].Containers);
        Assert.Equal(false, rules[1].Containers);
        Assert.Empty(rules[1].Exclusive);
        Assert.Empty(HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, new()).TestResourceRules);
    }

    [Theory]
    [InlineData("""{ "testResources": { "projects": "a" } }""", "expected an array")]
    [InlineData("""{ "testResources": [ { "projects": "a", "exclusiv": ["port:1"] } ] }""", "unknown property 'exclusiv'")]
    public void MalformedTestResourcesAreRejectedOnLoad(string json, string expected)
    {
        File.WriteAllText(Path.Combine(_root, ".piston.json"), json);

        var ex = Assert.Throws<InvalidOperationException>(() => HostHelpers.LoadConfig(_root));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "testResources": [ { "exclusive": ["port:1"] } ] }""", "testResources[0]' in .piston.json: 'projects'")]
    [InlineData("""{ "testResources": [ { "projects": "a", "exclusive": ["port:99999"] } ] }""", "testResources[0].exclusive")]
    [InlineData("""{ "testResources": [ { "projects": "a", "exclusive": ["db"] } ] }""", "testResources[0].exclusive")]
    [InlineData("""{ "testResources": [ { "projects": "a" }, { "projects": "b", "isolation": "machine" } ] }""", "testResources[1].isolation")]
    public void InvalidTestResourcesAreRejected(string json, string expected)
    {
        File.WriteAllText(Path.Combine(_root, ".piston.json"), json);
        var config = HostHelpers.LoadConfig(_root);

        var ex = Assert.Throws<InvalidOperationException>(() => HostHelpers.BuildOptions("/w/App.slnx", 0, null, false, 0, config));

        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    private string WriteSolution(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private void WriteConfig(string solution) =>
        File.WriteAllText(Path.Combine(_root, ".piston.json"),
            JsonSerializer.Serialize(new { solution, pipeName = "configured-pipe", stdio = true }));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
