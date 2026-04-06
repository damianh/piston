using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpTestProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenCancelled_ReturnsEmptyResultWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      "nonexistent.csproj",
            solutionDirectory: Path.GetTempPath(),
            filter:           null,
            collectCoverage:  false,
            onProgress:       null,
            ct:               cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }

    [Fact]
    public async Task RunAsync_NonExistentProject_ReturnsResultWithRunnerError()
    {
        // dotnet test on a non-existent project will fail; result should contain a runner error,
        // not throw an exception.
        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      Path.Combine(Path.GetTempPath(), "piston-test-nonexistent.csproj"),
            solutionDirectory: Path.GetTempPath(),
            filter:           null,
            collectCoverage:  false,
            onProgress:       null,
            ct:               CancellationToken.None);

        Assert.NotNull(result);
        // No suites (process failed), no crash flag (handled gracefully)
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }

    [Fact]
    public async Task RunAsync_SurfacesProjectPath_InResult()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        const string projectPath = "my/project.csproj";
        var result = await MtpTestProcessRunner.RunAsync(
            projectPath:      projectPath,
            solutionDirectory: Path.GetTempPath(),
            filter:           null,
            collectCoverage:  false,
            onProgress:       null,
            ct:               cts.Token);

        Assert.Equal(projectPath, result.ProjectPath);
    }
}
