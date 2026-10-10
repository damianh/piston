namespace Piston.Engine.Models;

public sealed record ImpactAnalysisResult(
    IReadOnlyList<string> AffectedProjectPaths,
    IReadOnlyList<string> AffectedTestProjectPaths,
    bool RequiresGraphRebuild,
    bool IsFullRun
)
{
    /// <summary>
    /// Optional list of test FQNs produced by Tier 3 (coverage-based) impact detection.
    /// When non-null and non-empty, the orchestrator narrows the test run to only these tests.
    /// When null, run all tests in <see cref="AffectedTestProjectPaths"/> (Tier 2 behavior).
    /// </summary>
    public IReadOnlyList<string>? AffectedTestFqns { get; init; }

    /// <summary>
    /// Projects that must be built before a selective test run: every affected non-test
    /// project plus every affected test project. Test projects must be rebuilt because
    /// runners use <c>--no-build</c>; building a test project also rebuilds its referenced
    /// projects and refreshes their copies in the test output directory.
    /// Empty for full runs (the whole solution is built instead).
    /// </summary>
    public IReadOnlyList<string> BuildTargetPaths =>
        IsFullRun
            ? []
            : AffectedProjectPaths
                .Concat(AffectedTestProjectPaths)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
}
