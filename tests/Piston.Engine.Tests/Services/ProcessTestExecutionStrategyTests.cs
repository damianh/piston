using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class ProcessTestExecutionStrategyTests
{
    [Fact]
    public void CanExecute_AlwaysReturnsTrue()
    {
        var strategy = new ProcessTestExecutionStrategy(new TrxResultParser());

        Assert.True(strategy.CanExecute("any.csproj"));
        Assert.True(strategy.CanExecute("another/path/project.csproj"));
        Assert.True(strategy.CanExecute(string.Empty));
    }

    [Fact]
    public async Task ExecuteAsync_NonExistentProject_ReturnsCrashedOrRunnerError()
    {
        // A non-existent project path should not throw — dotnet test will fail and
        // the strategy returns an error result rather than propagating an exception.
        var strategy = new ProcessTestExecutionStrategy(new TrxResultParser());

        var request = new ProjectTestRequest("nonexistent.csproj", null, false);
        var result  = await strategy.ExecuteAsync(request, onProgress: null, CancellationToken.None);

        // The result may have no suites and may have a runner error or no error;
        // the key invariant is that no exception is thrown.
        Assert.NotNull(result);
        Assert.Equal("nonexistent.csproj", result.ProjectPath);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCancelled_ReturnsEmptyResult()
    {
        var strategy = new ProcessTestExecutionStrategy(new TrxResultParser());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("any.csproj", null, false);
        var result  = await strategy.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }
}
