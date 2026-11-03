// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Piston.Engine.Services;

namespace Piston.Engine.Coverage;

/// <summary>
/// Bridges Cobertura XML coverage reports into the <see cref="ICoverageStore"/>.
/// Records the merged report and attributes each project's covered lines only to its own tests.
/// </summary>
internal sealed class CoverageProcessor(ICoberturaParser parser) : ICoverageProcessor
{
    public async Task<CoverageProcessingResult> ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store,
        bool isFullRun = false,
        string? sourceRoot = null,
        CoverageImpactOptions? impact = null)
    {
        // Aggregate all file→line coverage from all Cobertura reports
        // Key: filePath, Value: set of line numbers with hits > 0
        var coveredLines = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var reportFiles = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        var (parsed, result) = Parse(coberturaXmlPaths, sourceRoot);
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

        var unstableFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (parsedAny)
        {
            var files = new List<CoverageReportFile>();
            foreach (var file in reportFiles.Values)
            {
                if (impact is not null && ModifiedSince(file.FilePath, impact.StartedUtc))
                {
                    _ = unstableFiles.Add(file.FilePath);
                    continue;
                }

                var content = ImpactContent.TryRead(file.FilePath);
                if (impact is not null && (content is null || ModifiedSince(file.FilePath, impact.StartedUtc)))
                {
                    _ = unstableFiles.Add(file.FilePath);
                    continue;
                }

                files.Add(new CoverageReportFile(file, content is null ? null : ImpactContent.Hash(content), content));
            }

            if (unstableFiles.Count > 0)
            {
                result = new CoverageProcessingResult(result.Reports.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value with
                    {
                        SourceChanged = parsed.TryGetValue(pair.Key, out var report)
                            && report.Any(file => unstableFiles.Contains(file.FilePath)),
                    },
                    StringComparer.Ordinal));
                foreach (var file in unstableFiles)
                {
                    _ = coveredLines.Remove(file);
                    DiagnosticLog.Instance?.Write("Coverage", $"Skipped {file}: source changed or could not be read during the run.");
                }
            }

            var completeReports = result.Reports.Values.All(report =>
                report.Error is null && report.FileCount > 0 && !report.SourceChanged);
            await store.StoreReportAsync(runId, files, isFullRun && completeReports).ConfigureAwait(false);
        }

        if (impact is not null && store is IImpactMapStore impactMap)
        {
            var unverifiedReports = await WriteImpactAsync(runId, impact, parsed, impactMap).ConfigureAwait(false);
            result = MarkUnverified(result, unverifiedReports);
        }

        if (coveredLines.Count == 0 || testFqns.Count == 0)
        {
            return result;
        }

        var testCoverageMap = new Dictionary<string, IReadOnlyList<TestLineCoverage>>(StringComparer.Ordinal);
        if (impact is null)
        {
            IReadOnlyList<TestLineCoverage> entries =
                [.. coveredLines.SelectMany(file => file.Value.Select(line => new TestLineCoverage(file.Key, line, 1)))];
            foreach (var testFqn in testFqns)
            {
                testCoverageMap[testFqn] = entries;
            }
        }
        else
        {
            foreach (var unit in impact.Units)
            {
                IReadOnlyList<TestLineCoverage> entries = unit.ReportPaths.Where(parsed.ContainsKey)
                    .SelectMany(path => parsed[path])
                    .Where(file => !unstableFiles.Contains(file.FilePath))
                    .SelectMany(file => file.Lines.Where(line => line.HitCount > 0)
                        .Select(line => new TestLineCoverage(file.FilePath, line.LineNumber, 1)))
                    .Distinct()
                    .ToList();
                foreach (var testFqn in unit.TestFqns)
                {
                    testCoverageMap[testFqn] = testCoverageMap.TryGetValue(testFqn, out var earlier)
                        ? [.. earlier.Concat(entries).Distinct()]
                        : entries;
                }
            }
        }

        await store.StoreCoverageAsync(runId, testCoverageMap).ConfigureAwait(false);
        return result;
    }

    public async Task<CoverageProcessingResult> RecordImpactAsync(
        long runId, CoverageImpactOptions impact, IImpactMapStore store, string? sourceRoot = null)
    {
        var (parsed, result) = Parse([.. impact.Units.SelectMany(u => u.ReportPaths).Distinct(StringComparer.Ordinal)], sourceRoot);
        var unverifiedReports = await WriteImpactAsync(runId, impact, parsed, store).ConfigureAwait(false);
        return MarkUnverified(result, unverifiedReports);
    }

    private static CoverageProcessingResult MarkUnverified(CoverageProcessingResult result, IReadOnlySet<string> reports) =>
        reports.Count == 0 ? result : new CoverageProcessingResult(result.Reports.ToDictionary(
            pair => pair.Key,
            pair => reports.Contains(pair.Key) ? pair.Value with { SourceChanged = true } : pair.Value,
            StringComparer.Ordinal));

    private (Dictionary<string, IReadOnlyList<FileCoverage>> Parsed, CoverageProcessingResult Result) Parse(
        IReadOnlyList<string> reportPaths, string? sourceRoot)
    {
        var parsed = new Dictionary<string, IReadOnlyList<FileCoverage>>(StringComparer.Ordinal);
        var results = new Dictionary<string, CoverageReportResult>(StringComparer.Ordinal);
        foreach (var xmlPath in reportPaths)
        {
            try
            {
                var report = parser.Parse(xmlPath);
                var files = report.Files.Where(f => f.Lines.Count > 0 && (sourceRoot is null
                    || SourceFingerprinter.CoversPath(sourceRoot, f.FilePath))).ToList();
                parsed[xmlPath] = files;
                results[xmlPath] = new CoverageReportResult(files.Count, null);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException
                or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                var error = $"Cannot read coverage report {xmlPath}: {ex.Message}";
                results[xmlPath] = new CoverageReportResult(0, error);
                DiagnosticLog.Instance?.Write("Coverage", error);
            }
        }

        return (parsed, new CoverageProcessingResult(results));
    }

    private static async Task<IReadOnlySet<string>> WriteImpactAsync(
        long runId,
        CoverageImpactOptions impact,
        Dictionary<string, IReadOnlyList<FileCoverage>> parsed,
        IImpactMapStore impactMap)
    {
        var contents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var unverifiedReports = new HashSet<string>(StringComparer.Ordinal);
        foreach (var unit in impact.Units)
        {
            if (unit.TestFqns.Count == 0 || unit.ReportPaths.Count == 0
                || unit.ReportPaths.Any(path => !parsed.TryGetValue(path, out var files) || files.Count == 0))
            {
                continue;
            }

            var spansByFile = new Dictionary<string, List<LineSpan>>(StringComparer.OrdinalIgnoreCase);
            var branchesByFile = new Dictionary<string, List<BranchLine>>(StringComparer.OrdinalIgnoreCase);
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
                    if (!spansByFile.TryGetValue(file.FilePath, out var allSpans))
                    {
                        spansByFile[file.FilePath] = allSpans = [];
                    }

                    var spans = file.GetExecutedSpans();
                    allSpans.AddRange(spans);

                    if (!branchesByFile.TryGetValue(file.FilePath, out var branchLines))
                    {
                        branchesByFile[file.FilePath] = branchLines = [];
                    }

                    branchLines.AddRange(file.Lines
                        .Where(line => line.BranchesTotal > 0)
                        .Select(line => new BranchLine(line.LineNumber, line.BranchesCovered, line.BranchesTotal)));
                }
            }

            if (!measured)
            {
                continue;
            }

            var files = new List<ImpactFile>();
            var sourceUnverified = false;
            foreach (var path in spansByFile.Keys.Concat(branchesByFile.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!contents.TryGetValue(path, out var content))
                {
                    // A file edited after the build started may not be what ran; leave it unmapped so a
                    // change to it is not judged against the wrong snapshot.
                    contents[path] = content = ModifiedSince(path, impact.StartedUtc) ? null : ImpactContent.TryRead(path);
                    if (content is not null && ModifiedSince(path, impact.StartedUtc))
                    {
                        contents[path] = content = null;
                    }
                }

                if (content is not null)
                {
                    files.Add(new ImpactFile(
                        path,
                        LineSpan.Normalize(spansByFile.GetValueOrDefault(path) ?? []),
                        content,
                        branchesByFile.TryGetValue(path, out var branches) ? BranchLine.Merge(branches) : []));
                }
                else
                {
                    sourceUnverified = true;
                }
            }

            if (sourceUnverified)
            {
                unverifiedReports.UnionWith(unit.ReportPaths);
                continue;
            }

            await impactMap.RecordImpactAsync(new ImpactRecording(
                unit.ProjectPath, impact.Granularity, unit.UnitKey, runId, unit.TestFqns, files, unit.Merge, unit.MethodsComplete)
            {
                TestResultKeys = unit.TestResultKeys,
            }).ConfigureAwait(false);
        }

        return unverifiedReports;
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
