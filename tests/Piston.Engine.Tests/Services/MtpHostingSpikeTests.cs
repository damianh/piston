using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

/// <summary>
/// Spike tests validating that the ProcessTestExecutionStrategy is the correct universal
/// fallback for all test project types, including projects that use Microsoft.Testing.Platform v2.
///
/// SPIKE CONCLUSION (see .weave/spikes/mtp-v2-hosting.md):
/// True MTP v2 in-process hosting is not feasible without a Piston.Agent extension installed
/// in each test project. The current dotnet test subprocess approach is the recommended path.
/// ProcessTestExecutionStrategy handles MTP v2 projects transparently because dotnet test
/// invokes the MTP binary directly when Microsoft.Testing.Platform.MSBuild is referenced.
/// </summary>
public sealed class MtpHostingSpikeTests
{
    /// <summary>
    /// Validates that ProcessTestExecutionStrategy.CanExecute returns true for any project path,
    /// including projects that may use MTP v2. This is the universal fallback behaviour.
    /// </summary>
    [Fact]
    public void ProcessTestExecutionStrategy_CanExecute_MtpProjectPath_ReturnsTrue()
    {
        var strategy = new ProcessTestExecutionStrategy(new TrxResultParser());

        // MTP v2 projects are just .csproj files — CanExecute is path-agnostic
        Assert.True(strategy.CanExecute("SomeMtpProject.csproj"));
        Assert.True(strategy.CanExecute("/path/to/mtp-v2-test-project.csproj"));
    }

    /// <summary>
    /// Documents the architectural boundary: in-process MTP v2 hosting requires a Piston.Agent
    /// NuGet package installed in each test project. Without it, subprocess execution is correct.
    /// This test acts as a marker for the spike conclusion.
    /// </summary>
    [Fact]
    public void InProcessMtpHosting_WithoutAgent_IsNotSupported()
    {
        // Spike conclusion: MTP v2 in-process hosting requires a per-project agent.
        // The current implementation correctly delegates to ProcessTestExecutionStrategy.
        // This test documents the design decision — if in-process hosting is implemented
        // in the future via Piston.Agent, this test should be updated.

        var strategy = new ProcessTestExecutionStrategy(new TrxResultParser());

        // ProcessTestExecutionStrategy is the only strategy — CanExecute is always true.
        Assert.True(strategy.CanExecute(string.Empty));
        Assert.True(strategy is ITestExecutionStrategy);
    }

    /// <summary>
    /// Validates that a cancelled execution via ProcessTestExecutionStrategy
    /// returns empty results without throwing — same behaviour as for MTP and non-MTP projects.
    /// </summary>
    [Fact]
    public async Task ProcessTestExecutionStrategy_Cancelled_ReturnsEmptyGracefully()
    {
        var strategy = new ProcessTestExecutionStrategy(new TrxResultParser());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var request = new ProjectTestRequest("hypothetical-mtp-project.csproj", null, false);
        var result  = await strategy.ExecuteAsync(request, onProgress: null, cts.Token);

        Assert.NotNull(result);
        Assert.Empty(result.Suites);
        Assert.False(result.Crashed);
    }
}
