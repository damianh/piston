using System.Text.Json;
using Piston.Hosting;
using Xunit;

namespace Piston.Protocol.Tests;

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
        WriteSolution("Other.slnx");
        WriteSolution("Another.sln");
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
        WriteSolution("Discovered.slnx");
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
        WriteSolution("Wrong.csproj");
        WriteConfig(value);

        var error = Assert.Throws<InvalidOperationException>(() => HostHelpers.ResolveSolutionPath(null, _root));

        Assert.Contains(".piston.json", error.Message);
    }

    [Fact]
    public void MalformedConfigReportsErrorRatherThanIgnoringSolution()
    {
        WriteSolution("Discovered.slnx");
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
        WriteSolution("First.slnx");
        WriteSolution("Second.sln");
        Assert.Contains("Multiple solution files", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(null, _root)).Message);
    }

    [Fact]
    public void InvalidExplicitArgumentDoesNotFallBackToConfig()
    {
        WriteConfig("Configured.slnx");
        WriteSolution("Configured.slnx");
        Assert.Contains("Solution file not found", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(new FileInfo(Path.Combine(_root, "Missing.slnx")), _root)).Message);
        var wrongPath = WriteSolution("Wrong.csproj");
        Assert.Contains("Expected a .sln", Assert.Throws<InvalidOperationException>(
            () => HostHelpers.ResolveSolutionPath(new FileInfo(wrongPath), _root)).Message);
    }

    private string WriteSolution(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private void WriteConfig(string solution) =>
        File.WriteAllText(Path.Combine(_root, ".piston.json"),
            JsonSerializer.Serialize(new { solution, pipeName = "configured-pipe", stdio = true }));

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
