// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// Bridges Cobertura XML coverage reports into the <see cref="ICoverageStore"/>.
/// Records the merged report, and uses run-level granularity for attribution: all tests
/// in the run are attributed to every covered file/line in the Cobertura reports.
/// </summary>
internal sealed class CoverageProcessor : ICoverageProcessor
{
    private readonly ICoberturaParser _parser;

    public CoverageProcessor(ICoberturaParser parser) => _parser = parser;

    public async Task ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store,
        bool isFullRun = false)
    {
        if (coberturaXmlPaths.Count == 0)
        {
            return;
        }

        // Aggregate all file→line coverage from all Cobertura reports
        // Key: filePath, Value: set of line numbers with hits > 0
        var coveredLines = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var reportFiles = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        var parsedAny = false;

        foreach (var xmlPath in coberturaXmlPaths)
        {
            CoberturaReport report;
            try
            {
                report = _parser.Parse(xmlPath);
            }
            catch
            {
                // Malformed XML — skip this report
                continue;
            }

            parsedAny = true;
            foreach (var fileCoverage in report.Files)
            {
                reportFiles[fileCoverage.FilePath] = reportFiles.TryGetValue(fileCoverage.FilePath, out var seen)
                    ? MergeFile(seen, fileCoverage)
                    : fileCoverage;

                if (!coveredLines.TryGetValue(fileCoverage.FilePath, out var lineSet))
                {
                    lineSet = [];
                    coveredLines[fileCoverage.FilePath] = lineSet;
                }

                foreach (var line in fileCoverage.Lines)
                {
                    if (line.HitCount > 0)
                    {
                        _ = lineSet.Add(line.LineNumber);
                    }
                }
            }
        }

        if (parsedAny)
        {
            var files = reportFiles.Values
                .Select(f => new CoverageReportFile(f, CoverageReportFile.ComputeContentHash(f.FilePath)))
                .ToList();
            await store.StoreReportAsync(runId, files, isFullRun).ConfigureAwait(false);
        }

        if (coveredLines.Count == 0 || testFqns.Count == 0)
        {
            return;
        }

        // Run granularity: every test in the run is credited with every covered line. All tests share one
        // list so the store keeps the lines once rather than once per test.
        IReadOnlyList<TestLineCoverage> entries =
        [
            .. coveredLines.SelectMany(file => file.Value.Select(line => new TestLineCoverage(file.Key, line, 1))),
        ];
        var testCoverageMap = new Dictionary<string, IReadOnlyList<TestLineCoverage>>(StringComparer.Ordinal);
        foreach (var testFqn in testFqns)
        {
            testCoverageMap[testFqn] = entries;
        }

        await store.StoreCoverageAsync(runId, testCoverageMap).ConfigureAwait(false);
    }

    /// <summary>Combines two test projects' coverage of the same file: hits add up, branches take the best.</summary>
    private static FileCoverage MergeFile(FileCoverage a, FileCoverage b)
    {
        var lines = a.Lines.ToDictionary(l => l.LineNumber);
        foreach (var line in b.Lines)
        {
            lines[line.LineNumber] = lines.TryGetValue(line.LineNumber, out var old)
                ? new LineCoverage(
                    line.LineNumber,
                    old.HitCount + line.HitCount,
                    Math.Max(old.BranchesCovered, line.BranchesCovered),
                    Math.Max(old.BranchesTotal, line.BranchesTotal))
                : line;
        }

        return new FileCoverage(a.FilePath, [.. lines.Values.OrderBy(l => l.LineNumber)], a.Module ?? b.Module);
    }
}
