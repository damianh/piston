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
}
