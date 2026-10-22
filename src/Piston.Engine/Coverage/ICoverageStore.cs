// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// Persistence contract for code coverage data.
/// Implementations store test FQN → file path → line number mappings
/// so Tier 3 impact detection can query which tests cover changed lines.
/// </summary>
internal interface ICoverageStore : IDisposable
{
    /// <summary>
    /// Creates the <c>.piston/</c> directory (if needed) and initializes the SQLite database.
    /// Must be called before any other operation.
    /// </summary>
    /// <param name="solutionDirectory">Absolute path to the directory containing the watched solution file.</param>
    Task InitializeAsync(string solutionDirectory);

    /// <summary>
    /// Generates a monotonically increasing run identifier. Call once per pipeline run,
    /// before <see cref="StoreCoverageAsync"/>.
    /// </summary>
    long CreateRunId();

    /// <summary>
    /// Bulk-inserts test-to-line coverage data for a completed run.
    /// </summary>
    /// <param name="runId">Run identifier returned by <see cref="CreateRunId"/>.</param>
    /// <param name="testCoverageMap">
    /// Dictionary mapping test FQN → list of (filePath, lineNumber, hitCount) entries.
    /// </param>
    Task StoreCoverageAsync(long runId, IReadOnlyDictionary<string, IReadOnlyList<TestLineCoverage>> testCoverageMap);

    /// <summary>
    /// Returns all distinct test FQNs that cover any line in the given file (non-stale only).
    /// </summary>
    IReadOnlyList<string> GetTestsCoveringFile(string filePath);

    /// <summary>
    /// Returns all distinct test FQNs that cover any line between
    /// <paramref name="startLine"/> and <paramref name="endLine"/> inclusive (non-stale only).
    /// </summary>
    IReadOnlyList<string> GetTestsCoveringLines(string filePath, int startLine, int endLine);

    /// <summary>
    /// Returns true when the store contains any non-stale coverage entries for the given file.
    /// </summary>
    bool HasCoverageData(string filePath);

    /// <summary>
    /// Marks all coverage entries for <paramref name="filePath"/> as stale.
    /// Stale entries are excluded from Tier 3 queries until refreshed by a new test run.
    /// </summary>
    Task MarkFileStaleAsync(string filePath);

    /// <summary>
    /// Records a run's merged line and branch coverage in the solution-wide coverage report.
    /// </summary>
    /// <param name="runId">Run identifier returned by <see cref="CreateRunId"/>.</param>
    /// <param name="files">Each covered file with the hash of the content it was measured against.</param>
    /// <param name="isFullRun">
    /// True when every test ran unfiltered: the report is replaced. Otherwise only files whose content is
    /// unchanged are merged (taking the higher counts); other files are replaced and flagged partial.
    /// </param>
    Task StoreReportAsync(long runId, IReadOnlyList<CoverageReportFile> files, bool isFullRun);

    /// <summary>Returns the totals for every file in the coverage report, ordered by path.</summary>
    IReadOnlyList<CoverageFileSummary> GetReportFiles();

    /// <summary>Returns the stored totals for <paramref name="filePath"/>, or null when it has no coverage.</summary>
    CoverageFileSummary? GetReportFile(string filePath);

    /// <summary>Returns the stored line coverage for <paramref name="filePath"/>, ordered by line number.</summary>
    IReadOnlyList<LineCoverage> GetReportLines(string filePath);
}
