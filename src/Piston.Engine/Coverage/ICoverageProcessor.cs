// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>The result of reading a single coverage report.</summary>
internal sealed record CoverageReportResult(int FileCount, string? Error, bool SourceChanged = false);

/// <summary>The reports read by a coverage operation, indexed by their original paths.</summary>
internal sealed record CoverageProcessingResult(IReadOnlyDictionary<string, CoverageReportResult> Reports);

/// <summary>
/// Bridges Cobertura XML output from test runs into the coverage store.
/// </summary>
internal interface ICoverageProcessor
{
    /// <summary>
    /// Parses each Cobertura XML file, records the merged line and branch coverage in the store's
    /// coverage report, and attributes covered lines to each project's tests when impact units are provided.
    /// Otherwise, all covered lines are attributed to the provided test FQNs (run-level granularity).
    /// </summary>
    /// <param name="runId">The run identifier from <see cref="ICoverageStore.CreateRunId"/>.</param>
    /// <param name="coberturaXmlPaths">Paths to the Cobertura XML files produced by coverlet.</param>
    /// <param name="testFqns">Tests represented by the run, including skipped tests for conservative selection.</param>
    /// <param name="store">The coverage store to write into.</param>
    /// <param name="isFullRun">True when every test ran unfiltered, so the report replaces the stored one.</param>
    /// <param name="sourceRoot">
    /// When set, only watched source inputs under this directory are recorded, excluding build outputs and code
    /// the tests load from elsewhere (e.g. fixture projects in a temp directory).
    /// </param>
    /// <param name="impact">
    /// When set and <paramref name="store"/> keeps an impact map, records what each unit executed.
    /// </param>
    Task<CoverageProcessingResult> ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store,
        bool isFullRun = false,
        string? sourceRoot = null,
        CoverageImpactOptions? impact = null);

    /// <summary>
    /// Records only what each unit in <paramref name="impact"/> executed, leaving the coverage report untouched:
    /// for measurements of part of a test suite, such as one class.
    /// </summary>
    Task<CoverageProcessingResult> RecordImpactAsync(
        long runId, CoverageImpactOptions impact, Impact.IImpactMapStore store, string? sourceRoot = null);
}
