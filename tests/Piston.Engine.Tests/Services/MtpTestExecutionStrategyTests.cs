using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpTestExecutionStrategyTests
{
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

        strategy.CanExecute("specific.csproj");

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
        var result  = await strategy.ExecuteAsync(request, onProgress: null, cts.Token);

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
        var result  = await strategy.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
    }
}
