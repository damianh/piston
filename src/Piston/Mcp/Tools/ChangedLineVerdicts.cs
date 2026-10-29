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
    /// The line's code changed after it was measured, or (for a range) the file differs from the working tree: the
    /// coverage does not describe the line. Lines in unchanged C# members of a changed file keep their remapped
    /// coverage instead.
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

    /// <summary>
    /// A run was publishing new coverage and test results while they were read, so the line's coverage and its
    /// tests' outcomes may come from different runs.
    /// </summary>
    ResultsUpdating,
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
/// <param name="IsStale">
/// The file changed after it was measured, or a range's version of it differs from the working tree. Lines in its
/// unchanged C# members may still have verdicts from remapped coverage.
/// </param>
/// <param name="IsPartial">Only a selective run measured the file, so tests that did not run may cover more of it.</param>
internal sealed record ChangedFileVerdicts(
    string FilePath, bool IsMeasured, bool IsStale, bool IsPartial, IReadOnlyList<ChangedLineResult> Lines)
{
    public ChangedLineTotals Totals => ChangedLineTotals.Of(Lines);

    /// <summary>The distinct failing tests credited with any of the file's lines, in ordinal order.</summary>
    /// <remarks>Lines credited to the same tests share one list, so each list is read once, not once per line.</remarks>
    public IReadOnlyList<string> FailingTests() =>
        [.. Lines
            .Select(l => l.FailingTests)
            .OfType<IReadOnlyList<string>>()
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<IReadOnlyList<string>>()
            .SelectMany(tests => tests)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}

/// <summary>Verdicts for every changed line of every changed source file.</summary>
internal sealed record ChangedLineCoverageResult(
    string Basis, IReadOnlyList<ChangedFileVerdicts> Files, IReadOnlyList<string> Warnings)
{
    public ChangedLineTotals Totals => ChangedLineTotals.Of(Files.SelectMany(f => f.Lines));

    /// <summary>Each changed file's hunks, by absolute path, when context lines were asked for; otherwise empty.</summary>
    public IReadOnlyDictionary<string, DiffFile> Diff { get; init; } = new Dictionary<string, DiffFile>();

    /// <summary>As <see cref="ChangedLinesResult.DivergedFiles"/>: files whose line numbers do not describe the files on disk.</summary>
    public IReadOnlySet<string> DivergedFiles { get; init; } = new HashSet<string>();

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
    /// <param name="settleTimeout">
    /// How long to wait for the changed files and the published results to hold still while they are read, so the
    /// changed lines, the coverage and the outcomes describe the same content and runs. When they do not settle in
    /// time, every changed line is reported as unknown: <see cref="UnknownReason.ResultsUpdating"/> while a run is
    /// publishing results, or <see cref="UnknownReason.Stale"/> while the files keep changing.
    /// </param>
    /// <param name="contextLines">
    /// Above zero, the result's <see cref="ChangedLineCoverageResult.Diff"/> has each changed file's hunks with this
    /// many lines of context, read with the same changes the verdicts were given for.
    /// </param>
    /// <exception cref="ArgumentException">The base or ref is invalid.</exception>
    /// <exception cref="InvalidOperationException">Git failed or no base was found.</exception>
    public static async Task<ChangedLineCoverageResult> GetAsync(
        IEngine engine, string solutionDirectory, string? @base, string? baseRef, CancellationToken ct,
        TimeSpan? settleTimeout = null, int contextLines = 0)
    {
        var state = engine.State;
        var runBefore = state.CurrentRun;
        var deadline = DateTimeOffset.UtcNow + (settleTimeout ?? DefaultSettleTimeout);
        var changes = await ChangedLines.GetCheckedAsync(solutionDirectory, @base, baseRef, contextLines, ct).ConfigureAwait(false);
        ChangedLineCoverageResult? result = null;
        var unsettled = UnknownReason.ResultsUpdating;
        while (true)
        {
            // The changed lines are numbered for the files' content when git read them, and coverage is judged
            // against the content when it is read: both must see the same content.
            var contents = ContentHashes(changes);
            var version = state.ResultsVersion;
            if (!state.IsPublishingResults)
            {
                var read = await ReadAsync(engine, changes, deadline, ct).ConfigureAwait(false);
                if (read is null)
                {
                    unsettled = UnknownReason.ResultsUpdating;
                    break;
                }

                if (!state.IsPublishingResults && state.ResultsVersion == version)
                {
                    var again = await ChangedLines.GetCheckedAsync(solutionDirectory, @base, baseRef, contextLines, ct)
                        .ConfigureAwait(false);
                    if (SameChanges(changes, again) && SameContents(contents, ContentHashes(changes)))
                    {
                        // The second read's hunks hold the content the verdicts were judged against.
                        changes = again;
                        result = read;
                        break;
                    }

                    changes = again;
                    unsettled = UnknownReason.Stale;
                }
                else
                {
                    unsettled = UnknownReason.ResultsUpdating;
                }
            }
            else
            {
                unsettled = UnknownReason.ResultsUpdating;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                break;
            }

            await Task.Delay(SettlePollInterval, ct).ConfigureAwait(false);
        }

        var warnings = new List<string>();
        if (result is null)
        {
            result = Unsettled(changes, unsettled);
            warnings.Add(unsettled == UnknownReason.Stale
                ? "The changed files kept changing while the verdicts were read, so every changed line is unknown " +
                  "(stale). Ask again once they are saved."
                : "Test results were being published while the verdicts were read, so every changed line is unknown " +
                  "(resultsUpdating). Ask again when the run finishes.");
        }

        if ((state.CurrentRun ?? runBefore) is { } run)
        {
            warnings.Add($"Run {run.RunId} was in progress; verdicts use the results available when they were read " +
                "and may change when it finishes.");
        }

        result = result with { Diff = changes.Diff, DivergedFiles = changes.DivergedFiles };
        return warnings.Count == 0 ? result : result with { Warnings = [.. result.Warnings, .. warnings] };
    }

    private static readonly TimeSpan DefaultSettleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>An unblocked read finishes well within this, even once the settle time is spent.</summary>
    private static readonly TimeSpan MinReadWait = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Classifies off the caller's thread: coverage reads wait for a run that is storing coverage, so give up at
    /// <paramref name="deadline"/> (but not before <see cref="MinReadWait"/>) and return null rather than wait for
    /// the run. Cancellation stops the wait.
    /// </summary>
    private static async Task<ChangedLineCoverageResult?> ReadAsync(
        IEngine engine, ChangedLinesResult changes, DateTimeOffset deadline, CancellationToken ct)
    {
        var read = Task.Run(() => Classify(changes, engine.GetFileCoverage, engine.GetCoveringTests,
            OutcomeLookup(engine.State, engine.GetTestProjectPaths())), ct);
        var remaining = deadline - DateTimeOffset.UtcNow;
        try
        {
            return await read.WaitAsync(remaining > MinReadWait ? remaining : MinReadWait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    /// <summary>A hash of each changed source file's content on disk, or null when it does not exist.</summary>
    private static Dictionary<string, string?> ContentHashes(ChangedLinesResult changes)
    {
        var hashes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in changes.Files.Keys.Where(IsCodeFile))
        {
            try
            {
                hashes[path] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                hashes[path] = null;
            }
        }

        return hashes;
    }

    private static bool SameContents(Dictionary<string, string?> before, Dictionary<string, string?> after) =>
        before.Count == after.Count
        && before.All(kv => after.TryGetValue(kv.Key, out var hash) && string.Equals(hash, kv.Value, StringComparison.Ordinal));

    private static bool SameChanges(ChangedLinesResult a, ChangedLinesResult b) =>
        string.Equals(a.Basis, b.Basis, StringComparison.Ordinal)
        && a.Files.Count == b.Files.Count
        && a.Files.All(kv => b.Files.TryGetValue(kv.Key, out var lines) && lines.SetEquals(kv.Value))
        && a.DivergedFiles.SetEquals(b.DivergedFiles);

    /// <summary>
    /// The changes could not be read against one consistent state, so no line gets a verdict. Built without reading
    /// coverage, which may still be held by a run storing it.
    /// </summary>
    private static ChangedLineCoverageResult Unsettled(ChangedLinesResult changes, UnknownReason reason) =>
        new(changes.Basis,
            [.. changes.Files
                .Where(f => IsCodeFile(f.Key) && f.Value.Count > 0)
                .OrderBy(f => f.Key, StringComparer.Ordinal)
                .Select(f => new ChangedFileVerdicts(f.Key, IsMeasured: false, IsStale: reason == UnknownReason.Stale,
                    IsPartial: false,
                    [.. f.Value.Select(n => new ChangedLineResult(n, ChangedLineVerdict.Unknown, reason))]))],
            changes.Warnings);

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

        // A changed file keeps the coverage of its unchanged C# members, remapped onto the current lines; the lines
        // the store cannot validate are listed. Without that list, a changed file has no valid lines.
        var staleLines = coverage.StaleLines;
        if (coverage.File.IsStale && staleLines.Count == 0)
        {
            return new ChangedFileVerdicts(path, IsMeasured: true, IsStale: true, coverage.File.IsPartial,
                [.. lines.Select(n => new ChangedLineResult(n, ChangedLineVerdict.Unknown, UnknownReason.Stale))]);
        }

        var byLine = coverage.Lines.ToDictionary(l => l.LineNumber);
        IReadOnlyList<(CoveringTest Test, CoveringTestOutcome Outcome)>? executors = null;
        var failingLists = new Dictionary<int[], IReadOnlyList<string>>(IndexSetComparer.Instance);
        var results = new List<ChangedLineResult>();
        foreach (var n in lines)
        {
            if (staleLines.Any(r => r.Start <= n && n <= r.End))
            {
                results.Add(new ChangedLineResult(n, ChangedLineVerdict.Unknown, UnknownReason.Stale));
            }
            else if (!byLine.TryGetValue(n, out var line))
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
                results.Add(ClassifyExecuted(line, executors, failingLists));
            }
        }

        return new ChangedFileVerdicts(path, IsMeasured: true, coverage.File.IsStale, coverage.File.IsPartial, results);
    }

    /// <summary>
    /// A line that ran is proven by the tests the impact map credits with it: current evidence whose executed
    /// ranges contain the line. Evidence recorded against other content cannot be placed on the line, so it may
    /// only confirm a verdict, never decide one. Lines credited to the same executors share one failing-test list
    /// from <paramref name="failingLists"/>, so memory grows with the distinct sets rather than with the lines.
    /// </summary>
    private static ChangedLineResult ClassifyExecuted(
        LineCoverage line,
        IReadOnlyList<(CoveringTest Test, CoveringTestOutcome Outcome)> executors,
        Dictionary<int[], IReadOnlyList<string>> failingLists)
    {
        var creditedIndices = new List<int>();
        for (var i = 0; i < executors.Count; i++)
        {
            var test = executors[i].Test;
            if (test.IsCurrent && test.ExecutedLines.Any(r => r.Start <= line.LineNumber && line.LineNumber <= r.End))
            {
                creditedIndices.Add(i);
            }
        }

        var credited = creditedIndices.Select(i => executors[i]).ToList();
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
            !e.Test.IsCurrent && e.Outcome is CoveringTestOutcome.Passed or CoveringTestOutcome.Failed
            && e.Outcome != verdictOutcome
            && !creditedTests.Contains((e.Test.ProjectPath, e.Test.TestFqn)));
        if (contradicted)
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.Unknown, UnknownReason.OutdatedEvidence, line);
        }

        if (hasPassed)
        {
            return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.CoveredByPassing, Coverage: line);
        }

        // Every credited executor that was not skipped failed, so the failing tests follow from the credited set.
        var key = creditedIndices.ToArray();
        if (!failingLists.TryGetValue(key, out var failing))
        {
            failingLists[key] = failing =
                [.. credited.Select(e => e.Test.TestFqn).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        }

        return new ChangedLineResult(line.LineNumber, ChangedLineVerdict.CoveredOnlyByFailing, Coverage: line,
            FailingTests: failing);
    }

    /// <summary>Compares sets of executor indices, listed in ascending order, by content.</summary>
    private sealed class IndexSetComparer : IEqualityComparer<int[]>
    {
        public static readonly IndexSetComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj)
        {
            var hash = new HashCode();
            foreach (var i in obj)
            {
                hash.Add(i);
            }

            return hash.ToHashCode();
        }
    }

    /// <summary>
    /// Looks up a covering test's latest outcome in <paramref name="state"/>, in the suite of its own project,
    /// identified by its solution-relative path. A test with no result there has
    /// <see cref="CoveringTestOutcome.NoResult"/>, even when another project has a test of the same name. Results
    /// recorded without a project identity are matched by suite name (the project's file name), unless the project
    /// shares its file name with another of <paramref name="testProjectPaths"/>: their results cannot be told apart.
    /// A test that ran under several frameworks failed if any result failed, and passed if every result passed or
    /// was skipped (but not all skipped).
    /// </summary>
    public static Func<CoveringTest, CoveringTestOutcome> OutcomeLookup(
        PistonState state, IEnumerable<string>? testProjectPaths = null)
    {
        var byProject = new Dictionary<(string ProjectId, string Fqn), List<TestStatus>>();
        var bySuiteName = new Dictionary<(string Suite, string Fqn), List<TestStatus>>();
        var projectIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var suite in state.TestSuites)
        {
            if (suite.ProjectId is { } projectId)
            {
                _ = projectIds.Add(projectId);
            }

            foreach (var test in suite.Tests)
            {
                if (suite.ProjectId is { } id)
                {
                    Add(byProject, (id, test.FullyQualifiedName), test.Status);
                }
                else
                {
                    Add(bySuiteName, (suite.Name, test.FullyQualifiedName), test.Status);
                }
            }
        }

        var solutionDirectory = state.SolutionPath is { } solutionPath
            ? Path.GetDirectoryName(Path.GetFullPath(solutionPath))
            : null;
        var sharedSuites = (testProjectPaths ?? [])
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key!)
            .ToHashSet(StringComparer.Ordinal);

        return test =>
        {
            if (solutionDirectory is not null)
            {
                var projectId = Path.GetRelativePath(solutionDirectory, Path.GetFullPath(test.ProjectPath, solutionDirectory))
                    .Replace('\\', '/');
                if (projectIds.Contains(projectId))
                {
                    return Combine(byProject.GetValueOrDefault((projectId, test.TestFqn)));
                }
            }

            var suite = Path.GetFileNameWithoutExtension(test.ProjectPath);
            return sharedSuites.Contains(suite)
                ? CoveringTestOutcome.NoResult
                : Combine(bySuiteName.GetValueOrDefault((suite, test.TestFqn)));
        };

        static void Add<TKey>(Dictionary<TKey, List<TestStatus>> results, TKey key, TestStatus status)
            where TKey : notnull
        {
            if (!results.TryGetValue(key, out var list))
            {
                results[key] = list = [];
            }

            list.Add(status);
        }

        static CoveringTestOutcome Combine(List<TestStatus>? statuses) => statuses switch
        {
            null or [] => CoveringTestOutcome.NoResult,
            _ when statuses.Contains(TestStatus.Failed) => CoveringTestOutcome.Failed,
            _ when statuses.TrueForAll(s => s == TestStatus.Skipped) => CoveringTestOutcome.Skipped,
            _ when statuses.TrueForAll(s => s is TestStatus.Passed or TestStatus.Skipped) => CoveringTestOutcome.Passed,
            _ => CoveringTestOutcome.NoResult,
        };
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

    /// <summary>The camelCase wire name of a test outcome.</summary>
    public static string Name(CoveringTestOutcome outcome) => outcome switch
    {
        CoveringTestOutcome.Passed => "passed",
        CoveringTestOutcome.Failed => "failed",
        CoveringTestOutcome.Skipped => "skipped",
        _ => "noResult",
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
        UnknownReason.ResultsUpdating => "resultsUpdating",
        _ => null,
    };
}
