namespace Piston.Engine.Coverage;

/// <summary>
/// Bridges Cobertura XML coverage reports into the <see cref="ICoverageStore"/>.
/// Uses suite-level granularity: all tests in the suite are attributed to every
/// covered file/line in the Cobertura report.
/// </summary>
internal sealed class CoverageProcessor : ICoverageProcessor
{
    private readonly ICoberturaParser _parser;

    public CoverageProcessor(ICoberturaParser parser)
    {
        _parser = parser;
    }

    public async Task ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store)
    {
        if (coberturaXmlPaths.Count == 0 || testFqns.Count == 0)
            return;

        // Aggregate all file→line coverage from all Cobertura reports
        // Key: filePath, Value: set of line numbers with hits > 0
        var coveredLines = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);

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

            foreach (var fileCoverage in report.Files)
            {
                if (!coveredLines.TryGetValue(fileCoverage.FilePath, out var lineSet))
                {
                    lineSet = [];
                    coveredLines[fileCoverage.FilePath] = lineSet;
                }

                foreach (var line in fileCoverage.Lines)
                {
                    if (line.HitCount > 0)
                        lineSet.Add(line.LineNumber);
                }
            }
        }

        if (coveredLines.Count == 0)
            return;

        // Build the test coverage map: each test FQN → all covered file+line entries
        var testCoverageMap = new Dictionary<string, IReadOnlyList<TestLineCoverage>>(
            StringComparer.Ordinal);

        foreach (var testFqn in testFqns)
        {
            var entries = new List<TestLineCoverage>();

            foreach (var (filePath, lines) in coveredLines)
            {
                foreach (var lineNumber in lines)
                    entries.Add(new TestLineCoverage(filePath, lineNumber, 1));
            }

            testCoverageMap[testFqn] = entries;
        }

        await store.StoreCoverageAsync(runId, testCoverageMap).ConfigureAwait(false);
    }
}
