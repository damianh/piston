// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;

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
        bool isFullRun = false,
        string? sourceRoot = null,
        CoverageImpactOptions? impact = null)
    {
        if (coberturaXmlPaths.Count == 0)
        {
            return;
        }

        // Aggregate all file→line coverage from all Cobertura reports
        // Key: filePath, Value: set of line numbers with hits > 0
        var coveredLines = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var reportFiles = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        var parsed = Parse(coberturaXmlPaths, sourceRoot);
        var parsedAny = parsed.Count > 0;
        foreach (var fileCoverage in parsed.Values.SelectMany(f => f))
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

        if (parsedAny)
        {
            var files = reportFiles.Values
                .Select(f =>
                {
                    var content = ImpactContent.TryRead(f.FilePath);
                    return new CoverageReportFile(f, content is null ? null : ImpactContent.Hash(content), content);
                })
                .ToList();
            await store.StoreReportAsync(runId, files, isFullRun).ConfigureAwait(false);
        }

        if (impact is not null && store is IImpactMapStore impactMap)
        {
            await WriteImpactAsync(runId, impact, parsed, impactMap).ConfigureAwait(false);
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

    public async Task RecordImpactAsync(long runId, CoverageImpactOptions impact, IImpactMapStore store, string? sourceRoot = null)
    {
        var parsed = Parse([.. impact.Units.SelectMany(u => u.ReportPaths).Distinct(StringComparer.Ordinal)], sourceRoot);
        await WriteImpactAsync(runId, impact, parsed, store).ConfigureAwait(false);
    }

    /// <summary>Parses each report, keeping only files under <paramref name="sourceRoot"/>; malformed reports are skipped.</summary>
    private Dictionary<string, IReadOnlyList<FileCoverage>> Parse(IReadOnlyList<string> reportPaths, string? sourceRoot)
    {
        var rootPrefix = sourceRoot is null ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot)) + Path.DirectorySeparatorChar;
        var parsed = new Dictionary<string, IReadOnlyList<FileCoverage>>(StringComparer.Ordinal);
        foreach (var xmlPath in reportPaths)
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

            parsed[xmlPath] = [.. report.Files.Where(f => rootPrefix is null
                || Path.GetFullPath(f.FilePath).StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))];
        }

        return parsed;
    }

    private static async Task WriteImpactAsync(
        long runId,
        CoverageImpactOptions impact,
        Dictionary<string, IReadOnlyList<FileCoverage>> parsed,
        IImpactMapStore impactMap)
    {
        var contents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var unit in impact.Units)
        {
            if (unit.TestFqns.Count == 0)
            {
                continue;
            }

            var spansByFile = new Dictionary<string, List<LineSpan>>(StringComparer.OrdinalIgnoreCase);
            var measured = false;
            foreach (var reportPath in unit.ReportPaths)
            {
                if (!parsed.TryGetValue(reportPath, out var reportFiles))
                {
                    continue;
                }

                measured = true;
                foreach (var file in reportFiles)
                {
                    var spans = file.GetExecutedSpans();
                    if (spans.Count == 0)
                    {
                        continue;
                    }

                    if (!spansByFile.TryGetValue(file.FilePath, out var all))
                    {
                        spansByFile[file.FilePath] = all = [];
                    }

                    all.AddRange(spans);
                }
            }

            if (!measured)
            {
                continue;
            }

            var files = new List<ImpactFile>();
            foreach (var (path, spans) in spansByFile)
            {
                if (!contents.TryGetValue(path, out var content))
                {
                    // A file edited after the build started may not be what ran; leave it unmapped so a
                    // change to it is not judged against the wrong snapshot.
                    contents[path] = content = ModifiedSince(path, impact.StartedUtc) ? null : ImpactContent.TryRead(path);
                }

                if (content is not null)
                {
                    files.Add(new ImpactFile(path, LineSpan.Normalize(spans), content));
                }
            }

            await impactMap.RecordImpactAsync(new ImpactRecording(
                unit.ProjectPath, impact.Granularity, unit.UnitKey, runId, unit.TestFqns, files, unit.Merge, unit.MethodsComplete)).ConfigureAwait(false);
        }
    }

    private static bool ModifiedSince(string path, DateTime utc)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path) > utc;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
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

        return new FileCoverage(a.FilePath, [.. lines.Values.OrderBy(l => l.LineNumber)], a.Module ?? b.Module)
        {
            ExecutedSpans = a.ExecutedSpans is null && b.ExecutedSpans is null
                ? null
                : LineSpan.Normalize([.. a.GetExecutedSpans(), .. b.GetExecutedSpans()]),
        };
    }
}
