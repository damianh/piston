// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;

namespace Piston.Engine.Tests.Coverage;

/// <summary>
/// In-memory stub implementing <see cref="ICoverageStore"/> for use in unit tests.
/// </summary>
internal sealed class StubCoverageStore : ICoverageStore
{
    // Stored calls for assertion
    public List<(long RunId, IReadOnlyDictionary<string, IReadOnlyList<TestLineCoverage>> Map)> StoredCoverage { get; } = [];
    public List<string> MarkedStaleFiles { get; } = [];

    // Pre-configured query responses
    public Dictionary<string, List<string>> TestsCoveringFile { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> TestsCoveringLines { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> FilesWithCoverage { get; } = new(StringComparer.OrdinalIgnoreCase);

    private long _runCounter;

    public Task InitializeAsync(string solutionDirectory) => Task.CompletedTask;

    public long CreateRunId() => Interlocked.Increment(ref _runCounter);

    public Task StoreCoverageAsync(long runId, IReadOnlyDictionary<string, IReadOnlyList<TestLineCoverage>> testCoverageMap)
    {
        StoredCoverage.Add((runId, testCoverageMap));
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetTestsCoveringFile(string filePath) =>
        TestsCoveringFile.TryGetValue(filePath, out var tests) ? tests : [];

    public IReadOnlyList<string> GetTestsCoveringLines(string filePath, int startLine, int endLine) =>
        TestsCoveringLines.TryGetValue(filePath, out var tests) ? tests : [];

    public bool HasCoverageData(string filePath) => FilesWithCoverage.Contains(filePath);

    public Task MarkFileStaleAsync(string filePath)
    {
        MarkedStaleFiles.Add(filePath);
        return Task.CompletedTask;
    }

    public List<(long RunId, IReadOnlyList<CoverageReportFile> Files, bool IsFullRun)> StoredReports { get; } = [];

    public Task StoreReportAsync(long runId, IReadOnlyList<CoverageReportFile> files, bool isFullRun)
    {
        StoredReports.Add((runId, files, isFullRun));
        return Task.CompletedTask;
    }

    public IReadOnlyList<CoverageFileSummary> GetReportFiles() => [];

    public CoverageFileSummary? GetReportFile(string filePath) => null;

    public IReadOnlyList<LineCoverage> GetReportLines(string filePath) => [];

    public IReadOnlyList<LineSpan> GetReportStaleLines(string filePath) => [];

    public void Dispose() { }
}
