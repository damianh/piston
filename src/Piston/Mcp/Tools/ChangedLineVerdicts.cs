// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

/// <summary>What the coverage evidence says about one changed line.</summary>
internal enum ChangedLineVerdict
{
    /// <summary>The line ran, and every current test credited with executing it passed.</summary>
    CoveredByPassing,

    /// <summary>The line ran, and every current test credited with executing it failed.</summary>
    CoveredOnlyByFailing,

    /// <summary>The line is executable and no test that ran executed it.</summary>
    Uncovered,

    /// <summary>The file was measured as it is now and the line has no executable code.</summary>
    NonExecutable,

    /// <summary>The evidence cannot prove anything about the line; see <see cref="UnknownReason"/>.</summary>
    Unknown,
}

/// <summary>Why a changed line's verdict is <see cref="ChangedLineVerdict.Unknown"/>.</summary>
internal enum UnknownReason
{
    None,

    /// <summary>
    /// The file changed after it was measured, or (for a range) differs from the working tree: the coverage does not
    /// describe the changed lines.
    /// </summary>
    Stale,

    /// <summary>No coverage run measured the file, e.g. it is not compiled into a tested assembly.</summary>
    NotMeasured,

    /// <summary>The line ran, but no current impact-map evidence says which tests executed it.</summary>
    NoTestEvidence,

    /// <summary>The tests credited with executing the line include both passing and failing tests.</summary>
    MixedEvidence,

    /// <summary>A test credited with executing the line has no pass or fail result, or every credited test was skipped.</summary>
    TestsNotRun,

    /// <summary>
    /// Evidence recorded against an older version of the file may credit other tests with the line, and their
    /// outcomes would change the verdict.
    /// </summary>
    OutdatedEvidence,
}

/// <summary>A test's latest outcome, combined over the frameworks it ran under.</summary>
internal enum CoveringTestOutcome
{
    Passed,
    Failed,

    /// <summary>Skipped everywhere it ran: it executed nothing, so it is not evidence either way.</summary>
    Skipped,

    /// <summary>No result, e.g. not run since it was discovered or still running.</summary>
    NoResult,
}

/// <summary>The verdict for one changed line.</summary>
/// <param name="Line">1-based line number in the current file.</param>
/// <param name="Coverage">The line's aggregate coverage, when the file was measured as it is now and the line is executable.</param>
/// <param name="FailingTests">For <see cref="ChangedLineVerdict.CoveredOnlyByFailing"/>: the failing tests credited with the line.</param>
internal sealed record ChangedLineResult(
    int Line,
    ChangedLineVerdict Verdict,
    UnknownReason Reason = UnknownReason.None,
    LineCoverage? Coverage = null,
    IReadOnlyList<string>? FailingTests = null)
{
    public bool HasPartialBranches =>
        Coverage is { HitCount: > 0, BranchesTotal: > 0 } c && c.BranchesCovered < c.BranchesTotal;
}

/// <summary>Counts of changed lines per verdict.</summary>
internal sealed record ChangedLineTotals(
    int ChangedLines, int CoveredByPassing, int CoveredOnlyByFailing, int Uncovered, int NonExecutable, int Unknown)
{
    public static ChangedLineTotals Of(IEnumerable<ChangedLineResult> lines)
    {
        int total = 0, passing = 0, failing = 0, uncovered = 0, nonExecutable = 0, unknown = 0;
        foreach (var line in lines)
        {
            total++;
            switch (line.Verdict)
            {
                case ChangedLineVerdict.CoveredByPassing: passing++; break;
                case ChangedLineVerdict.CoveredOnlyByFailing: failing++; break;
                case ChangedLineVerdict.Uncovered: uncovered++; break;
                case ChangedLineVerdict.NonExecutable: nonExecutable++; break;
                default: unknown++; break;
            }
        }

        return new(total, passing, failing, uncovered, nonExecutable, unknown);
    }
}

/// <summary>The verdicts for one changed source file.</summary>
/// <param name="FilePath">Absolute path.</param>
/// <param name="IsMeasured">The coverage report has the file.</param>
/// <param name="IsStale">The file changed after it was measured, or a range's version of it differs from the working tree.</param>
/// <param name="IsPartial">Only a selective run measured the file, so tests that did not run may cover more of it.</param>
internal sealed record ChangedFileVerdicts(
    string FilePath, bool IsMeasured, bool IsStale, bool IsPartial, IReadOnlyList<ChangedLineResult> Lines)
{
    public ChangedLineTotals Totals => ChangedLineTotals.Of(Lines);
}

/// <summary>Verdicts for every changed line of every changed source file.</summary>
internal sealed record ChangedLineCoverageResult(
    string Basis, IReadOnlyList<ChangedFileVerdicts> Files, IReadOnlyList<string> Warnings)
{
    public ChangedLineTotals Totals => ChangedLineTotals.Of(Files.SelectMany(f => f.Lines));

    /// <summary>Unknown lines per reason, every reason included.</summary>
    public IReadOnlyDictionary<UnknownReason, int> UnknownReasons
    {
        get
        {
            var counts = Enum.GetValues<UnknownReason>().Where(r => r != UnknownReason.None).ToDictionary(r => r, _ => 0);
            foreach (var line in Files.SelectMany(f => f.Lines).Where(l => l.Verdict == ChangedLineVerdict.Unknown))
            {
                counts[line.Reason]++;
            }

            return counts;
        }
    }
}

/// <summary>
/// Classifies each changed line by what the coverage report, the impact map and the latest test outcomes prove
/// about it. Stale or outdated evidence never counts as covered.
/// </summary>
internal static class ChangedLineVerdicts
{
    /// <summary>Source file types whose changed lines can be measured.</summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb" };

    public static bool IsCodeFile(string path) => CodeExtensions.Contains(Path.GetExtension(path));

    /// <summary>Reads git changes and classifies them against <paramref name="engine"/>'s coverage and test results.</summary>
    /// <exception cref="ArgumentException">The base or ref is invalid.</exception>
    /// <exception cref="InvalidOperationException">Git failed or no base was found.</exception>
    public static async Task<ChangedLineCoverageResult> GetAsync(
        IEngine engine, string solutionDirectory, string? @base, string? baseRef, CancellationToken ct)
    {
        var changes = await ChangedLines.GetCheckedAsync(solutionDirectory, @base, baseRef, ct).ConfigureAwait(false);
        var result = Classify(changes, engine.GetFileCoverage, engine.GetCoveringTests, OutcomeLookup(engine.State));
        return engine.State.CurrentRun is { } run
            ? result with
            {
                Warnings = [.. result.Warnings,
                    $"Run {run.RunId} is in progress; verdicts use the previous results and may change when it finishes."],
            }
            : result;
    }

    public static ChangedLineCoverageResult Classify(
        ChangedLinesResult changes,
        Func<string, FileCoverageSnapshot?> getCoverage,
        Func<string, IReadOnlyList<CoveringTest>?> getCoveringTests,
        Func<CoveringTest, CoveringTestOutcome> getOutcome)
    {
        var files = new List<ChangedFileVerdicts>();
        foreach (var (path, lines) in changes.Files.OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            if (!IsCodeFile(path) || lines.Count == 0)
            {
                continue;
            }

            files.Add(changes.DivergedFiles.Contains(path)
                ? Diverged(path, lines, getCoverage)
                : ClassifyFile(path, lines, getCoverage, getCoveringTests, getOutcome));
        }

        return new ChangedLineCoverageResult(changes.Basis, files, changes.Warnings);
    }

    /// <summary>The changed lines are numbered for content other than the working tree's, which coverage measures.</summary>
    private static ChangedFileVerdicts Diverged(
        string path, IEnumerable<int> lines, Func<string, FileCoverageSnapshot?> getCoverage)
    {
        var coverage = getCoverage(path);
        return new ChangedFileVerdicts(path, IsMeasured: coverage is not null, IsStale: true, coverage?.File.IsPartial ?? false,
            [.. lines.Select(n => new ChangedLineResult(n, ChangedLineVerdict.Unknown, UnknownReason.Stale))]);
    }

    private static ChangedFileVerdicts ClassifyFile(
        string path,
        IEnumerable<int> lines,
        Func<string, FileCoverageSnapshot?> getCoverage,
        Func<string, IReadOnlyList<CoveringTest>?> getCoveringTests,
        Func<CoveringTest, CoveringTestOutcome> getOutcome)
    {
        if (getCoverage(path) is not { } coverage)
        {
            return new ChangedFileVerdicts(path, IsMeasured: false, IsStale: false, IsPartial: false,
                [.. lines.Select(n => new ChangedLineResult(n, ChangedLineVerdict.Unknown, UnknownReason.NotMeasured))]);
        }

        if (coverage.File.IsStale)
        {
            return new ChangedFileVerdicts(path, IsMeasured: true, IsStale: true, coverage.File.IsPartial,
                [.. lines.Select(n => new ChangedLineResult(n, ChangedLineVerdict.Unknown, UnknownReason.Stale))]);
        }

        var byLine = coverage.Lines.ToDictionary(l => l.LineNumber);
        IReadOnlyList<(CoveringTest Test, CoveringTestOutcome Outcome)>? executors = null;
        var results = new List<ChangedLineResult>();
        foreach (var n in lines)
        {
            if (!byLine.TryGetValue(n, out var line))
            {
                results.Add(new ChangedLineResult(n, ChangedLineVerdict.NonExecutable));
            }
            else if (line.HitCount == 0)
            {
                results.Add(new ChangedLineResult(n, ChangedLineVerdict.Uncovered, Coverage: line));
            }
            else
            {
                executors ??= [.. (getCoveringTests(path) ?? []).Select(t => (t, getOutcome(t)))];
                results.Add(ClassifyExecuted(line, executors));
            }
        }

        return new ChangedFileVerdicts(path, IsMeasured: true, IsStale: false, coverage.File.IsPartial, results);
    }

    /// <summary>
    /// A line that ran is proven by the tests the impact map credits with it: current evidence whose executed
    /// ranges contain the line. Evidence recorded against other content cannot be placed on the line, so it may
    /// only confirm a verdict, never decide one.
    /// </summary>
    private static ChangedLineResult ClassifyExecuted(
        LineCoverage line, IReadOnlyList<(CoveringTest Test, CoveringTestOutcome Outcome)> executors)
    {
        var credited = executors
            .Where(e => e.Test.IsCurrent && e.Test.ExecutedLines.Any(r => r.Start <= line.LineNumber && line.LineNumber <= r.End))
            .ToList();
        if (credited.Count == 0)
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.Unknown, UnknownReason.NoTestEvidence, line);
        }

        _ = credited.RemoveAll(e => e.Outcome == CoveringTestOutcome.Skipped);
        if (credited.Count == 0)
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.Unknown, UnknownReason.TestsNotRun, line);
        }

        var hasPassed = credited.Any(e => e.Outcome == CoveringTestOutcome.Passed);
        var hasFailed = credited.Any(e => e.Outcome == CoveringTestOutcome.Failed);
        if (hasPassed && hasFailed)
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.Unknown, UnknownReason.MixedEvidence, line);
        }

        if (credited.Any(e => e.Outcome == CoveringTestOutcome.NoResult))
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.Unknown, UnknownReason.TestsNotRun, line);
        }

        var verdictOutcome = hasPassed ? CoveringTestOutcome.Passed : CoveringTestOutcome.Failed;
        var creditedTests = credited.Select(e => (e.Test.ProjectPath, e.Test.TestFqn)).ToHashSet();
        var contradicted = executors.Any(e =>
            !e.Test.IsCurrent && e.Outcome != verdictOutcome && e.Outcome != CoveringTestOutcome.Skipped
            && !creditedTests.Contains((e.Test.ProjectPath, e.Test.TestFqn)));
        if (contradicted)
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.Unknown, UnknownReason.OutdatedEvidence, line);
        }

        return hasPassed
            ? new ChangedLineResult(line.LineNumber, ChangedLineVerdict.CoveredByPassing, Coverage: line)
            : new ChangedLineResult(line.LineNumber, ChangedLineVerdict.CoveredOnlyByFailing, Coverage: line,
                FailingTests: [.. credited.Select(e => e.Test.TestFqn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Looks up a covering test's latest outcome in <paramref name="state"/>: in the suite of its project, or in any
    /// suite when that suite has no result for it. A test that ran under several frameworks failed if any result
    /// failed, and passed if every result passed or was skipped (but not all skipped).
    /// </summary>
    public static Func<CoveringTest, CoveringTestOutcome> OutcomeLookup(PistonState state)
    {
        var bySuite = new Dictionary<(string Suite, string Fqn), List<TestStatus>>();
        var byName = new Dictionary<string, List<TestStatus>>(StringComparer.Ordinal);
        foreach (var suite in state.TestSuites)
        {
            foreach (var test in suite.Tests)
            {
                Add(bySuite, (suite.Name, test.FullyQualifiedName), test.Status);
                Add(byName, test.FullyQualifiedName, test.Status);
            }
        }

        return test =>
        {
            var suite = Path.GetFileNameWithoutExtension(test.ProjectPath);
            var statuses = bySuite.GetValueOrDefault((suite, test.TestFqn)) ?? byName.GetValueOrDefault(test.TestFqn);
            return statuses switch
            {
                null or [] => CoveringTestOutcome.NoResult,
                _ when statuses.Contains(TestStatus.Failed) => CoveringTestOutcome.Failed,
                _ when statuses.TrueForAll(s => s == TestStatus.Skipped) => CoveringTestOutcome.Skipped,
                _ when statuses.TrueForAll(s => s is TestStatus.Passed or TestStatus.Skipped) => CoveringTestOutcome.Passed,
                _ => CoveringTestOutcome.NoResult,
            };
        };

        static void Add<TKey>(Dictionary<TKey, List<TestStatus>> map, TKey key, TestStatus status)
            where TKey : notnull
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = [];
            }

            list.Add(status);
        }
    }

    /// <summary>The camelCase wire name of a verdict.</summary>
    public static string Name(ChangedLineVerdict verdict) => verdict switch
    {
        ChangedLineVerdict.CoveredByPassing => "coveredByPassing",
        ChangedLineVerdict.CoveredOnlyByFailing => "coveredOnlyByFailing",
        ChangedLineVerdict.Uncovered => "uncovered",
        ChangedLineVerdict.NonExecutable => "nonExecutable",
        _ => "unknown",
    };

    /// <summary>The camelCase wire name of an unknown reason, or null for none.</summary>
    public static string? Name(UnknownReason reason) => reason switch
    {
        UnknownReason.Stale => "stale",
        UnknownReason.NotMeasured => "notMeasured",
        UnknownReason.NoTestEvidence => "noTestEvidence",
        UnknownReason.MixedEvidence => "mixedEvidence",
        UnknownReason.TestsNotRun => "testsNotRun",
        UnknownReason.OutdatedEvidence => "outdatedEvidence",
        _ => null,
    };
}
