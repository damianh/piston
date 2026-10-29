// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Models;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class ChangedLineVerdictsTests
{
    private const string File = "/src/A.cs";
    private const string Project = "/tests/A.Tests/A.Tests.csproj";

    private readonly Dictionary<string, FileCoverageSnapshot> _coverage = [];
    private readonly Dictionary<string, IReadOnlyList<CoveringTest>> _tests = [];
    private readonly Dictionary<string, CoveringTestOutcome> _outcomes = new(StringComparer.Ordinal);

    // Lines 1-4 executable: 1 and 2 ran, 3 did not, 4 ran with half its branches; line 5 has no code.
    private void Measure(bool stale = false, bool partial = false, params (int Start, int End)[] staleLines) =>
        _coverage[File] = new FileCoverageSnapshot(
            new CoverageFileSummary(File, "A", 4, 3, 2, 1, stale, partial, "hash", 1, DateTimeOffset.UnixEpoch),
            [new(1, 3), new(2, 1), new(3, 0), new(4, 1, 1, 2)])
        {
            StaleLines = [.. staleLines.Select(r => new CoveredLineRange(r.Start, r.End))],
        };

    private void Covers(string fqn, CoveringTestOutcome outcome, bool current = true, params (int Start, int End)[] lines)
    {
        var tests = _tests.TryGetValue(File, out var existing) ? existing.ToList() : [];
        tests.Add(new CoveringTest(fqn, Project, "test", [.. lines.Select(l => new CoveredLineRange(l.Start, l.End))], current));
        _tests[File] = tests;
        _outcomes[fqn] = outcome;
    }

    private ChangedLineCoverageResult Classify(params int[] lines) =>
        ChangedLineVerdicts.Classify(
            new ChangedLinesResult("working tree", new Dictionary<string, SortedSet<int>> { [File] = [.. lines] }),
            _coverage.GetValueOrDefault,
            _tests.GetValueOrDefault,
            t => _outcomes.GetValueOrDefault(t.TestFqn, CoveringTestOutcome.NoResult));

    private ChangedLineResult Line(int line) =>
        Assert.Single(Assert.Single(Classify(line).Files).Lines);

    [Fact]
    public void LineExecutedOnlyByPassingTests_IsCoveredByPassing()
    {
        Measure();
        Covers("T.Pass1", CoveringTestOutcome.Passed, lines: (1, 2));
        Covers("T.Pass2", CoveringTestOutcome.Passed, lines: (1, 1));

        var line = Line(1);

        Assert.Equal(ChangedLineVerdict.CoveredByPassing, line.Verdict);
        Assert.Equal(UnknownReason.None, line.Reason);
        Assert.Equal(3, line.Coverage!.HitCount);
    }

    [Fact]
    public void LinesCreditedToTheSameFailingTests_ShareOneFailingTestList()
    {
        Measure();
        Covers("T.Fail1", CoveringTestOutcome.Failed, lines: (1, 2));
        Covers("T.Fail2", CoveringTestOutcome.Failed, lines: [(1, 2), (4, 4)]);
        Covers("T.Fail3", CoveringTestOutcome.Failed, lines: (4, 4));

        var lines = Assert.Single(Classify(1, 2, 4).Files).Lines;

        Assert.All(lines, l => Assert.Equal(ChangedLineVerdict.CoveredOnlyByFailing, l.Verdict));
        Assert.Same(lines[0].FailingTests, lines[1].FailingTests);
        Assert.Equal(["T.Fail1", "T.Fail2"], lines[0].FailingTests!);
        Assert.Equal(["T.Fail2", "T.Fail3"], lines[2].FailingTests!);
    }

    [Fact]
    public void FileFailingTests_MergesTheLinesSharedListsIntoOneOrderedList()
    {
        Measure();
        Covers("T.Fail2", CoveringTestOutcome.Failed, lines: [(1, 2), (4, 4)]);
        Covers("T.Fail1", CoveringTestOutcome.Failed, lines: (1, 2));
        Covers("T.Fail3", CoveringTestOutcome.Failed, lines: (4, 4));

        var file = Assert.Single(Classify(1, 2, 4).Files);

        Assert.Equal(["T.Fail1", "T.Fail2", "T.Fail3"], file.FailingTests());
    }

    [Fact]
    public void LineExecutedOnlyByFailingTests_IsCoveredOnlyByFailing_AndNamesThem()
    {
        Measure();
        Covers("T.FailB", CoveringTestOutcome.Failed, lines: (1, 2));
        Covers("T.FailA", CoveringTestOutcome.Failed, lines: (2, 2));
        Covers("T.Elsewhere", CoveringTestOutcome.Passed, lines: (4, 4));

        var line = Line(2);

        Assert.Equal(ChangedLineVerdict.CoveredOnlyByFailing, line.Verdict);
        Assert.Equal(["T.FailA", "T.FailB"], line.FailingTests);
    }

    [Fact]
    public void SkippedTestsAreIgnoredWhenOthersDecide()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 1));
        Covers("T.Skip", CoveringTestOutcome.Skipped, lines: (1, 1));

        Assert.Equal(ChangedLineVerdict.CoveredByPassing, Line(1).Verdict);
    }

    [Fact]
    public void ExecutableLineNoTestRan_IsUncovered()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 4));

        var line = Line(3);

        Assert.Equal(ChangedLineVerdict.Uncovered, line.Verdict);
        Assert.Equal(0, line.Coverage!.HitCount);
    }

    [Fact]
    public void LineWithoutCodeInMeasuredFile_IsNonExecutable()
    {
        Measure();

        var line = Line(5);

        Assert.Equal(ChangedLineVerdict.NonExecutable, line.Verdict);
        Assert.Null(line.Coverage);
    }

    [Fact]
    public void UnmeasuredFile_IsUnknownNotMeasured()
    {
        var file = Assert.Single(Classify(1, 2).Files);

        Assert.False(file.IsMeasured);
        Assert.All(file.Lines, l => Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.NotMeasured), (l.Verdict, l.Reason)));
    }

    [Fact]
    public void StaleFile_IsNeverCovered()
    {
        Measure(stale: true);
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 5));

        var file = Assert.Single(Classify(1, 3, 5).Files);

        Assert.True(file.IsStale);
        Assert.All(file.Lines, l => Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.Stale), (l.Verdict, l.Reason)));
        Assert.All(file.Lines, l => Assert.Null(l.Coverage));
    }

    [Fact]
    public void StaleFile_LinesOutsideItsStaleLines_UseTheRemappedCoverage()
    {
        // Lines 1-3 are in unchanged members, so the store remapped their coverage; line 4's member changed.
        Measure(stale: true, staleLines: (4, 5));
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 2));

        var file = Assert.Single(Classify(1, 3, 4, 5, 6).Files);

        Assert.True(file.IsStale);
        Assert.Equal(
            [
                (1, ChangedLineVerdict.CoveredByPassing, UnknownReason.None),
                (3, ChangedLineVerdict.Uncovered, UnknownReason.None),
                (4, ChangedLineVerdict.Unknown, UnknownReason.Stale),
                (5, ChangedLineVerdict.Unknown, UnknownReason.Stale),
                (6, ChangedLineVerdict.NonExecutable, UnknownReason.None),
            ],
            file.Lines.Select(l => (l.Line, l.Verdict, l.Reason)));
        Assert.Null(file.Lines[2].Coverage);
    }

    [Fact]
    public void RangeFileThatDiffersFromTheWorkingTree_IsNeverCovered()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 5));
        var changes = new ChangedLinesResult("main..HEAD", new Dictionary<string, SortedSet<int>> { [File] = [1, 3, 5] })
        {
            DivergedFiles = new HashSet<string> { File },
        };

        var file = Assert.Single(ChangedLineVerdicts.Classify(
            changes, _coverage.GetValueOrDefault, _tests.GetValueOrDefault, t => _outcomes[t.TestFqn]).Files);

        Assert.True(file.IsMeasured);
        Assert.True(file.IsStale);
        Assert.All(file.Lines, l => Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.Stale), (l.Verdict, l.Reason)));
    }

    [Fact]
    public void ExecutedLineWithoutImpactEvidence_IsUnknownNoTestEvidence()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (2, 2));

        var line = Line(1);

        Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.NoTestEvidence), (line.Verdict, line.Reason));
    }

    [Fact]
    public void ExecutedLineWhoseOnlyEvidenceIsOutdated_IsUnknownNoTestEvidence()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, current: false, lines: (1, 1));

        Assert.Equal(UnknownReason.NoTestEvidence, Line(1).Reason);
    }

    [Fact]
    public void LineExecutedByPassingAndFailingTests_IsUnknownMixedEvidence()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 1));
        Covers("T.Fail", CoveringTestOutcome.Failed, lines: (1, 1));

        var line = Line(1);

        Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.MixedEvidence), (line.Verdict, line.Reason));
    }

    [Fact]
    public void LineExecutedByTestWithoutResult_IsUnknownTestsNotRun()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 1));
        Covers("T.Pending", CoveringTestOutcome.NoResult, lines: (1, 1));

        Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.TestsNotRun), (Line(1).Verdict, Line(1).Reason));
    }

    [Fact]
    public void LineExecutedOnlyBySkippedTests_IsUnknownTestsNotRun()
    {
        Measure();
        Covers("T.Skip", CoveringTestOutcome.Skipped, lines: (1, 1));

        Assert.Equal(UnknownReason.TestsNotRun, Line(1).Reason);
    }

    [Fact]
    public void OutdatedEvidenceWithAnotherOutcome_IsUnknownOutdatedEvidence()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 1));
        Covers("T.OldFail", CoveringTestOutcome.Failed, current: false, lines: (10, 12));

        var line = Line(1);

        Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.OutdatedEvidence), (line.Verdict, line.Reason));
    }

    [Fact]
    public void OutdatedEvidenceAgreeingWithTheVerdict_DoesNotBlockIt()
    {
        Measure();
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 1));
        Covers("T.OldPass", CoveringTestOutcome.Passed, current: false, lines: (10, 12));
        Covers("T.OldSkip", CoveringTestOutcome.Skipped, current: false, lines: (10, 12));
        Covers("T.OldDeleted", CoveringTestOutcome.NoResult, current: false, lines: (10, 12));

        Assert.Equal(ChangedLineVerdict.CoveredByPassing, Line(1).Verdict);
    }

    [Fact]
    public void Totals_CountEveryVerdictAndUnknownReason()
    {
        Measure(partial: true);
        Covers("T.Pass", CoveringTestOutcome.Passed, lines: (1, 1));
        Covers("T.Fail", CoveringTestOutcome.Failed, lines: (2, 2));
        Covers("T.Mixed", CoveringTestOutcome.Passed, lines: (4, 4));
        Covers("T.Mixed2", CoveringTestOutcome.Failed, lines: (4, 4));
        var changes = new ChangedLinesResult(
            "HEAD",
            new Dictionary<string, SortedSet<int>>
            {
                [File] = [1, 2, 3, 4, 5],
                ["/src/New.cs"] = [1],
                ["/docs/README.md"] = [1],
            });

        var result = ChangedLineVerdicts.Classify(
            changes, _coverage.GetValueOrDefault, _tests.GetValueOrDefault, t => _outcomes[t.TestFqn]);

        Assert.Equal(["/src/A.cs", "/src/New.cs"], result.Files.Select(f => f.FilePath));
        Assert.True(result.Files[0].IsPartial);
        Assert.True(result.Files[0].Lines[3].HasPartialBranches);
        Assert.Equal(new ChangedLineTotals(6, 1, 1, 1, 1, 2), result.Totals);
        Assert.Equal(1, result.UnknownReasons[UnknownReason.MixedEvidence]);
        Assert.Equal(1, result.UnknownReasons[UnknownReason.NotMeasured]);
        Assert.Equal(0, result.UnknownReasons[UnknownReason.Stale]);
        Assert.DoesNotContain(UnknownReason.None, result.UnknownReasons.Keys);
    }

    [Fact]
    public void OutcomeLookup_CombinesFrameworksAndUsesOnlyTheTestsOwnSuite()
    {
        var state = new PistonState
        {
            TestSuites =
            [
                Suite("A.Tests", ("T.Pass", TestStatus.Passed), ("T.Pass", TestStatus.Skipped),
                    ("T.Tfm", TestStatus.Passed), ("T.Tfm", TestStatus.Failed),
                    ("T.Skip", TestStatus.Skipped), ("T.Skip", TestStatus.Skipped),
                    ("T.Running", TestStatus.Running), ("T.Shared", TestStatus.Passed)),
                Suite("B.Tests", ("T.Shared", TestStatus.Failed), ("T.OnlyInB", TestStatus.Failed)),
            ],
        };
        var lookup = ChangedLineVerdicts.OutcomeLookup(state);
        CoveringTestOutcome Outcome(string fqn) => lookup(new CoveringTest(fqn, Project, "test", [], true));

        Assert.Equal(CoveringTestOutcome.Passed, Outcome("T.Pass"));
        Assert.Equal(CoveringTestOutcome.Failed, Outcome("T.Tfm"));
        Assert.Equal(CoveringTestOutcome.Skipped, Outcome("T.Skip"));
        Assert.Equal(CoveringTestOutcome.NoResult, Outcome("T.Running"));
        Assert.Equal(CoveringTestOutcome.Passed, Outcome("T.Shared"));
        Assert.Equal(CoveringTestOutcome.NoResult, Outcome("T.OnlyInB"));
        Assert.Equal(CoveringTestOutcome.NoResult, Outcome("T.Missing"));
    }

    [Fact]
    public void OutcomeLookup_ProjectsSharingAFileName_HaveNoResult()
    {
        var state = new PistonState { TestSuites = [Suite("A.Tests", ("T.Pass", TestStatus.Passed))] };
        var lookup = ChangedLineVerdicts.OutcomeLookup(state, [Project, "/other/A.Tests/A.Tests.csproj", "/tests/B.Tests/B.Tests.csproj"]);

        // The other A.Tests project's results land in the same suite, so this one's cannot be told apart.
        Assert.Equal(CoveringTestOutcome.NoResult, lookup(new CoveringTest("T.Pass", Project, "test", [], true)));
        Assert.Equal(CoveringTestOutcome.Passed,
            ChangedLineVerdicts.OutcomeLookup(state, [Project, "/tests/B.Tests/B.Tests.csproj"])(
                new CoveringTest("T.Pass", Project, "test", [], true)));
    }

    [Fact]
    public void OutcomeLookup_MatchesResultsByProjectIdentity()
    {
        var other = "/other/A.Tests/A.Tests.csproj";
        var state = new PistonState
        {
            SolutionPath = "/App.slnx",
            TestSuites =
            [
                Suite("A.Tests", ("T.Pass", TestStatus.Passed)) with { ProjectId = "tests/A.Tests/A.Tests.csproj" },
                Suite("A.Tests", ("T.Pass", TestStatus.Failed), ("T.OnlyInOther", TestStatus.Passed))
                    with { ProjectId = "other/A.Tests/A.Tests.csproj" },
                Suite("A.Tests", ("T.Legacy", TestStatus.Passed)),
            ],
        };
        var lookup = ChangedLineVerdicts.OutcomeLookup(state, [Project, other]);
        CoveringTestOutcome Outcome(string fqn, string project) => lookup(new CoveringTest(fqn, project, "test", [], true));

        // Projects that share a file name are told apart by their solution-relative paths.
        Assert.Equal(CoveringTestOutcome.Passed, Outcome("T.Pass", Project));
        Assert.Equal(CoveringTestOutcome.Failed, Outcome("T.Pass", other));
        Assert.Equal(CoveringTestOutcome.NoResult, Outcome("T.OnlyInOther", Project));
        Assert.Equal(CoveringTestOutcome.NoResult, Outcome("T.Legacy", Project));

        // Results recorded without a project identity fall back to the suite name when it is unambiguous.
        var legacy = new CoveringTest("T.Legacy", "/legacy/A.Tests/A.Tests.csproj", "test", [], true);
        Assert.Equal(CoveringTestOutcome.NoResult, lookup(legacy));
        Assert.Equal(CoveringTestOutcome.Passed, ChangedLineVerdicts.OutcomeLookup(state, [legacy.ProjectPath])(legacy));
    }

    private static TestSuite Suite(string name, params (string Fqn, TestStatus Status)[] tests) =>
        new(name, [.. tests.Select(t => new TestResult(t.Fqn, t.Fqn, t.Status, TimeSpan.Zero, null, null, null, null))],
            DateTimeOffset.UnixEpoch, TimeSpan.Zero);
}
