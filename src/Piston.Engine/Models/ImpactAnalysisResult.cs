// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record ImpactAnalysisResult(
    IReadOnlyList<string> AffectedProjectPaths,
    IReadOnlyList<string> AffectedTestProjectPaths,
    bool RequiresGraphRebuild,
    bool IsFullRun
)
{
    /// <summary>
    /// Most tests a run narrows to by name; a larger <see cref="AffectedTestFqns"/> runs the affected test projects
    /// whole, to keep the test command line short.
    /// </summary>
    public const int MaxFilteredTests = 100;

    /// <summary>
    /// Optional list of test FQNs produced by Tier 3 (coverage-based) impact detection.
    /// When non-null and non-empty, the orchestrator narrows the test run to only these tests.
    /// When null, run all tests in <see cref="AffectedTestProjectPaths"/> (Tier 2 behavior).
    /// </summary>
    public IReadOnlyList<string>? AffectedTestFqns { get; init; }

    /// <summary>
    /// Why each test in <see cref="AffectedTestFqns"/> was selected, when the selection came from the impact map's
    /// changed-line comparison. Null for file-level coverage selection and for Tier 1/2 runs.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<TestSelectionReason>>? SelectionReasons { get; init; }

    /// <summary>One sentence saying why the analysis chose this scope, e.g. why it could not narrow to tests.</summary>
    public string? Explanation { get; init; }

    /// <summary>True when a run narrows to <see cref="AffectedTestFqns"/> by name rather than running whole projects.</summary>
    public bool NarrowsToTests => AffectedTestFqns is { Count: > 0 and <= MaxFilteredTests };

    /// <summary>
    /// <see cref="Explanation"/>, except when more tests were selected than a run narrows to and the affected projects
    /// run whole instead.
    /// </summary>
    public string? RunExplanation => AffectedTestFqns is { Count: > MaxFilteredTests } fqns
        ? $"{fqns.Count} tests were selected, more than the {MaxFilteredTests} a run narrows to, so tests are selected by project."
        : Explanation;

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

/// <summary>Why a test was selected: an edit touched a file it executed.</summary>
/// <param name="FilePath">The changed file.</param>
/// <param name="ChangedLines">The lines the edit touched in the measured version, e.g. <c>12-14</c>.</param>
/// <param name="ExecutedLines">
/// The executed range the edit overlapped, or null when the edit fell outside every executed range and the test
/// was selected because it executed the file at all.
/// </param>
/// <param name="Granularity">
/// What the evidence measured: <c>run</c> (every test in the project's run together), <c>class</c> or
/// <c>test</c>. Run- and class-level evidence over-selects; it does not mean this test executed the lines.
/// </param>
/// <param name="ProjectPath">The test project the evidence came from.</param>
public sealed record TestSelectionReason(
    string FilePath,
    string ChangedLines,
    string? ExecutedLines,
    string Granularity,
    string ProjectPath);
