using Piston.Engine.Coverage;
using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests;

public sealed class PistonEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("piston-clear-test-").FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClearResultsClearsPersistedCoverageAndNotifiesWithResetState(bool coverageEnabled)
    {
        var filePath = Path.Combine(_root, "Code.cs");
        using var store = new SqliteCoverageStore();
        await store.InitializeAsync(_root);
        await store.StoreCoverageAsync(store.CreateRunId(),
            new Dictionary<string, IReadOnlyList<TestLineCoverage>>
            {
                ["Tests.Passes"] = [new(filePath, 1, 1)],
            });
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = Path.Combine(_root, "Test.slnx"),
            CoverageEnabled = coverageEnabled,
        });
        engine.State.TestSuites = [new("Tests", [], DateTimeOffset.UtcNow, TimeSpan.Zero)];
        engine.State.LastRunTime = DateTimeOffset.UtcNow;
        engine.State.HasCoverageData = true;
        engine.State.CoverageImpactDetail = "Tier 3";
        engine.State.TestFilter = "KeepFilter";
        var notified = false;
        engine.State.StateChanged += () =>
        {
            notified = true;
            Assert.Empty(engine.State.TestSuites);
            Assert.Null(engine.State.LastRunTime);
            Assert.False(engine.State.HasCoverageData);
            Assert.Null(engine.State.CoverageImpactDetail);
            Assert.False(store.HasCoverageData(filePath));
        };

        engine.ClearResults();

        Assert.True(notified);
        Assert.Equal("KeepFilter", engine.State.TestFilter);
        Assert.Equal(coverageEnabled, engine.State.CoverageEnabled);
        Assert.Empty(store.GetTestsCoveringFile(filePath));
    }

    [Fact]
    public void ClearResultsWithoutDatabaseDoesNotCreateOne()
    {
        using var engine = new PistonEngine(new PistonOptions
        {
            SolutionPath = Path.Combine(_root, "Test.slnx"),
        });

        engine.ClearResults();
        engine.ClearResults();

        Assert.False(File.Exists(Path.Combine(_root, ".piston", "piston.db")));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
