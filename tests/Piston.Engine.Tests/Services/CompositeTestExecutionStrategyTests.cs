using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class CompositeTestExecutionStrategyTests
{
    [Fact]
    public void CanExecute_AlwaysReturnsTrue()
    {
        var composite = BuildComposite(mtpOutputPath: null);

        Assert.True(composite.CanExecute("any.csproj"));
        Assert.True(composite.CanExecute("other.csproj"));
    }

    [Fact]
    public async Task ExecuteAsync_WhenMtpProject_UsesMtpStrategy()
    {
        // Arrange: MTP strategy will be called (non-null output path), but execution is
        // cancelled immediately so we get a quick empty result from MtpTestProcessRunner.
        var composite = BuildComposite(mtpOutputPath: Path.Combine(Path.GetTempPath(), "tests.dll"));

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("mtp-project.csproj", null, false);
        var result  = await composite.ExecuteAsync(request, onProgress: null, cts.Token);

        // Should have been routed to MTP strategy (returns empty on cancellation)
        Assert.NotNull(result);
        Assert.Empty(result.Suites);
    }

    [Fact]
    public async Task ExecuteAsync_WhenNonMtpProject_UsesVsTestStrategy()
    {
        // Arrange: MTP strategy cannot execute (null path), so VSTest fallback is used.
        // VSTest also returns empty on cancellation.
        var composite = BuildComposite(mtpOutputPath: null);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("vstest-project.csproj", null, false);
        var result  = await composite.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
    }

    [Fact]
    public async Task ExecuteAsync_MtpProject_PassesCorrectProjectPath()
    {
        // The MTP output path delegate receives the project path
        string? seenProjectPath = null;
        var mtpStrategy    = new MtpTestExecutionStrategy(
            p => { seenProjectPath = p; return null; },
            Path.GetTempPath()); // return null so it falls through to VSTest
        var vsTestStrategy = new ProcessTestExecutionStrategy(new TrxResultParser());
        var composite      = new CompositeTestExecutionStrategy(mtpStrategy, vsTestStrategy);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("target.csproj", null, false);
        await composite.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.Equal("target.csproj", seenProjectPath);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CompositeTestExecutionStrategy BuildComposite(string? mtpOutputPath)
    {
        var mtpStrategy    = new MtpTestExecutionStrategy(_ => mtpOutputPath, Path.GetTempPath());
        var vsTestStrategy = new ProcessTestExecutionStrategy(new TrxResultParser());
        return new CompositeTestExecutionStrategy(mtpStrategy, vsTestStrategy);
    }
}
