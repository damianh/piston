// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Models;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class CoverageToolsTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-coverage-tools-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private static CoverageFileSummary Summary(string path, int total, int covered, bool stale = false, string module = "Lib") =>
        new(path, module, total, covered, 2, 1, stale, IsPartial: false, "hash", 1, DateTimeOffset.UnixEpoch);

    private StubEngine Engine()
    {
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        return engine;
    }

    [Fact]
    public void ParseAddedLines_ReadsNewSideOfEachHunk()
    {
        const string diff = """
            diff --git a/src/A.cs b/src/A.cs
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -3 +3 @@ class A
            -old
            +new
            @@ -10,0 +11,3 @@
            +x
            +y
            +z
            @@ -20,2 +23,0 @@
            -gone
            -gone
            diff --git a/Deleted.cs b/Deleted.cs
            --- a/Deleted.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -bye
            diff --git a/OnlyRemovals.cs b/OnlyRemovals.cs
            --- a/OnlyRemovals.cs
            +++ b/OnlyRemovals.cs
            @@ -4 +3,0 @@
            -x
            """;

        var result = ChangedLines.ParseAddedLines(diff);

        Assert.Equal(["src/A.cs"], result.Keys);
        Assert.Equal([3, 11, 12, 13], result["src/A.cs"]);
    }

    [Theory]
    [InlineData(new int[0], "")]
    [InlineData(new[] { 5 }, "5")]
    [InlineData(new[] { 9, 3, 4, 5, 7 }, "3-5, 7, 9")]
    public void FormatRanges_CollapsesRuns(int[] lines, string expected) =>
        Assert.Equal(expected, ChangedLines.FormatRanges(lines));

    [Theory]
    [InlineData("main", true)]
    [InlineData("origin/main", true)]
    [InlineData("HEAD~2", true)]
    [InlineData("@{u}", true)]
    [InlineData("-p", false)]
    [InlineData("--output=/tmp/x", false)]
    [InlineData("a b", false)]
    [InlineData("a;rm", false)]
    public void IsValidRevision_RejectsOptionsAndShellSyntax(string revision, bool valid) =>
        Assert.Equal(valid, ChangedLines.IsValidRevision(revision));

    [Fact]
    public void GetCoverage_WhenDisabled_Fails()
    {
        var tools = new CoverageTools(new StubEngine(), NullMcpCallRecorder.Instance);

        var ex = Assert.Throws<McpException>(() => tools.GetCoverage());

        Assert.Contains("disabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetCoverage_Summary_ReportsModulesAndWorstFiles()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([
            Summary(Path.Combine(_root, "src", "A.cs"), 10, 9),
            Summary(Path.Combine(_root, "src", "B.cs"), 10, 2, stale: true),
            Summary(Path.Combine(_root, "src", "C.cs"), 4, 4, module: "Other"),
        ]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(tools.GetCoverage(maxFiles: 1))!;

        Assert.Equal("piston.coverage/v1", json["schema"]!.GetValue<string>());
        Assert.Equal(62.5, json["totals"]!["linePercent"]!.GetValue<double>());
        Assert.Equal(1, json["staleFiles"]!.GetValue<int>());
        Assert.Equal(["Lib", "Other"], json["modules"]!.AsArray().Select(m => m!["module"]!.GetValue<string>()));
        var worst = Assert.Single(json["filesWithMostUncoveredLines"]!.AsArray())!;
        Assert.Equal("src/B.cs", worst["path"]!.GetValue<string>());
        Assert.True(worst["isStale"]!.GetValue<bool>());
    }

    [Fact]
    public void GetCoverage_File_ReportsUncoveredAndPartialRanges()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage["src/A.cs"] = new FileCoverageSnapshot(
            Summary(Path.Combine(_root, "src", "A.cs"), 5, 3),
            [new(1, 1), new(2, 0), new(3, 0), new(4, 2, 1, 2), new(6, 0)]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(tools.GetCoverage("src/A.cs"))!;

        Assert.Equal("src/A.cs", json["path"]!.GetValue<string>());
        Assert.Equal("2-3, 6", json["uncoveredLines"]!.GetValue<string>());
        var partial = Assert.Single(json["partialLines"]!.AsArray())!;
        Assert.Equal(4, partial["line"]!.GetValue<int>());
        Assert.Equal("1/2", partial["branches"]!.GetValue<string>());
    }

    [Fact]
    public void GetCoverage_File_ReportsUnvalidatedCurrentLines()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage["src/A.cs"] = new FileCoverageSnapshot(
            Summary(Path.Combine(_root, "src", "A.cs"), 2, 1, stale: true),
            [new(1, 1)])
        {
            StaleLines = [new CoveredLineRange(2, 3)],
        };
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(tools.GetCoverage("src/A.cs"))!;

        Assert.Equal("2-3", json["staleLines"]!.GetValue<string>());
    }

    [Fact]
    public void GetCoverage_UnmeasuredFile_Fails()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        _ = Assert.Throws<McpException>(() => tools.GetCoverage("src/Nope.cs"));
    }

    [Fact]
    public async Task GetChangedLineCoverage_GivesEachChangedLineAVerdict()
    {
        Git("init", "-q", "-b", "main");
        var measured = Path.Combine(_root, "Measured.cs");
        await File.WriteAllLinesAsync(measured, ["a", "b", "c", "d", "e", "g"], _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "x", _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");

        await File.WriteAllLinesAsync(measured, ["a", "B", "C", "D", "E", "f", "G"], _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "y", _ct);
        await File.WriteAllLinesAsync(Path.Combine(_root, "New.cs"), ["n"], _ct);

        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage[measured] = new FileCoverageSnapshot(
            Summary(measured, 5, 4),
            [new(2, 1), new(3, 0), new(4, 2), new(5, 1, 1, 2), new(7, 1)]);
        engine.CoveringTests[measured] =
        [
            Covering("Lib.Tests.Pass", 2, 2),
            Covering("Lib.Tests.Fail", 5, 5) with { Branches = [new(5, 1, 2)] },
            Covering("Lib.Tests.Fail", 5, 5) with
            {
                ProjectPath = Path.Combine(_root, "Other.Tests", "Other.Tests.csproj"),
                Branches = [new(5, 2, 2)],
            },
            Covering("Lib.Tests.Old", 5, 5, current: false) with { Branches = [new(5, 2, 2)] },
            Covering("Lib.Tests.Pass", 4, 4),
            Covering("Lib.Tests.Fail", 4, 4),
        ];
        engine.State.TestSuites =
        [
            new TestSuite("Lib.Tests",
                [Result("Lib.Tests.Pass", TestStatus.Passed), Result("Lib.Tests.Fail", TestStatus.Failed)],
                DateTimeOffset.UnixEpoch, TimeSpan.Zero),
            new TestSuite("Other.Tests", [Result("Lib.Tests.Fail", TestStatus.Failed)],
                DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ];
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.GetChangedLineCoverageAsync(ct: _ct))!;

        Assert.Equal("piston.changed-line-coverage/v2", json["schema"]!.GetValue<string>());
        var totals = json["totals"]!;
        Assert.Equal(7, totals["changedLines"]!.GetValue<int>());
        Assert.Equal(1, totals["coveredByPassing"]!.GetValue<int>());
        Assert.Equal(1, totals["coveredOnlyByFailing"]!.GetValue<int>());
        Assert.Equal(1, totals["uncovered"]!.GetValue<int>());
        Assert.Equal(1, totals["nonExecutable"]!.GetValue<int>());
        Assert.Equal(3, totals["unknown"]!.GetValue<int>());
        Assert.Equal(1, json["unknownReasons"]!["mixedEvidence"]!.GetValue<int>());
        Assert.Equal(1, json["unknownReasons"]!["noTestEvidence"]!.GetValue<int>());
        Assert.Equal(1, json["unknownReasons"]!["notMeasured"]!.GetValue<int>());
        Assert.Equal(0, json["unknownReasons"]!["stale"]!.GetValue<int>());

        var files = json["files"]!.AsArray();
        Assert.Equal(["Measured.cs", "New.cs"], files.Select(f => f!["path"]!.GetValue<string>()));
        var file = files[0]!;
        var branch = Assert.Single(file["branchLines"]!.AsArray())!;
        Assert.Equal((5, 1, 2), (
            branch["line"]!.GetValue<int>(),
            branch["covered"]!.GetValue<int>(),
            branch["total"]!.GetValue<int>()));
        var attributions = branch["tests"]!.AsArray();
        Assert.Equal(2, attributions.Count);
        var attributed = attributions.Single(test => test!["taken"]!.GetValue<int>() == 1)!;
        var other = attributions.Single(test => test!["taken"]!.GetValue<int>() == 2)!;
        Assert.Equal("Other.Tests/Other.Tests.csproj", other["project"]!.GetValue<string>());
        Assert.NotEqual(attributed["project"]!.GetValue<string>(), other["project"]!.GetValue<string>());
        Assert.Equal(("Lib.Tests.Fail", 1, 2, "test"), (
            attributed["name"]!.GetValue<string>(),
            attributed["taken"]!.GetValue<int>(),
            attributed["total"]!.GetValue<int>(),
            attributed["granularity"]!.GetValue<string>()));
        Assert.Equal("2", file["coveredByPassing"]!.GetValue<string>());
        Assert.Equal("5", file["coveredOnlyByFailing"]!.GetValue<string>());
        Assert.Equal("3", file["uncovered"]!.GetValue<string>());
        Assert.Equal("6", file["nonExecutable"]!.GetValue<string>());
        Assert.Equal(
            [("4", "mixedEvidence"), ("7", "noTestEvidence")],
            file["unknown"]!.AsArray().Select(u => (u!["lines"]!.GetValue<string>(), u["reason"]!.GetValue<string>())));
        Assert.Equal(5, Assert.Single(file["partialBranches"]!.AsArray())!["line"]!.GetValue<int>());
        Assert.Equal(["Lib.Tests.Fail"], file["failingTests"]!.AsArray().Select(t => t!.GetValue<string>()));
        var unmeasured = files[1]!;
        Assert.False(unmeasured["measured"]!.GetValue<bool>());
        Assert.Equal("notMeasured", Assert.Single(unmeasured["unknown"]!.AsArray())!["reason"]!.GetValue<string>());
    }

    [Fact]
    public void ChangedLineCoverage_GroupsBranchAttributionsByLineInProjectAndNameOrder()
    {
        var file = new ChangedFileVerdicts(Path.Combine(_root, "Code.cs"), true, false, false,
        [
            new(2, ChangedLineVerdict.CoveredByPassing, Coverage: new(2, 1, 1, 2)),
            new(3, ChangedLineVerdict.Uncovered, Coverage: new(3, 0, 0, 2)),
            new(4, ChangedLineVerdict.CoveredByPassing, Coverage: new(4, 1, 2, 2)),
        ])
        {
            BranchTests =
            [
                Covering("Tests.A", 2, 4) with
                {
                    ProjectPath = Path.Combine(_root, "B.csproj"),
                    Branches = [new(2, 2, 2), new(4, 1, 2)],
                },
                Covering("Tests.Z", 2, 4) with
                {
                    ProjectPath = Path.Combine(_root, "A.csproj"),
                    Branches = [new(2, 0, 2), new(99, 1, 2)],
                },
                Covering("Tests.A", 2, 4) with
                {
                    ProjectPath = Path.Combine(_root, "A.csproj"),
                    Branches = [new(4, 2, 2), new(2, 1, 2)],
                },
            ],
        };

        var json = CoverageTools.ChangedLineCoverage(new("working", [file], []), _root);

        var lines = json["files"]![0]!["branchLines"]!.AsArray();
        Assert.Equal([2, 3, 4], lines.Select(line => line!["line"]!.GetValue<int>()));
        Assert.Equal(
            [("A.csproj", "Tests.A", 1), ("A.csproj", "Tests.Z", 0), ("B.csproj", "Tests.A", 2)],
            Attributions(lines[0]!));
        Assert.Empty(lines[1]!["tests"]!.AsArray());
        Assert.Equal(
            [("A.csproj", "Tests.A", 2), ("B.csproj", "Tests.A", 1)],
            Attributions(lines[2]!));

        static IEnumerable<(string Project, string Name, int Taken)> Attributions(JsonNode line) =>
            line["tests"]!.AsArray().Select(test => (
                test!["project"]!.GetValue<string>(),
                test["name"]!.GetValue<string>(),
                test["taken"]!.GetValue<int>()));
    }

    [Fact]
    public async Task GetChangedLineCoverage_FileEditedAfterTheRun_IsUnknownStaleNotCovered()
    {
        Git("init", "-q", "-b", "main");
        var path = Path.Combine(_root, "Edited.cs");
        await File.WriteAllLinesAsync(path, ["a", "b"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(path, ["a", "B"], _ct);

        // The run measured the edit and a passing test executed it.
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 2, 2), [new(1, 1), new(2, 1)]);
        engine.CoveringTests[path] = [Covering("Lib.Tests.Pass", 1, 2)];
        engine.State.TestSuites =
        [
            new TestSuite("Lib.Tests", [Result("Lib.Tests.Pass", TestStatus.Passed)], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ];
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);
        var before = JsonNode.Parse(await tools.GetChangedLineCoverageAsync(ct: _ct))!;
        Assert.Equal("2", before["files"]![0]!["coveredByPassing"]!.GetValue<string>());

        // Editing the line again makes the measured content stale (the engine compares content hashes) and the
        // impact evidence outdated.
        await File.WriteAllLinesAsync(path, ["a", "B2"], _ct);
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 2, 2, stale: true), [new(1, 1), new(2, 1)]);
        engine.CoveringTests[path] = [Covering("Lib.Tests.Pass", 1, 2, current: false)];

        var after = JsonNode.Parse(await tools.GetChangedLineCoverageAsync(ct: _ct))!;

        Assert.Equal(0, after["totals"]!["coveredByPassing"]!.GetValue<int>());
        Assert.Equal(1, after["unknownReasons"]!["stale"]!.GetValue<int>());
        var file = after["files"]![0]!;
        Assert.True(file["isStale"]!.GetValue<bool>());
        Assert.Equal("", file["coveredByPassing"]!.GetValue<string>());
        var unknown = Assert.Single(file["unknown"]!.AsArray())!;
        Assert.Equal(("2", "stale"), (unknown["lines"]!.GetValue<string>(), unknown["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task GetChangedLineCoverage_RangeWithUncommittedEdits_IsUnknownStale()
    {
        Git("init", "-q", "-b", "main");
        var path = Path.Combine(_root, "A.cs");
        var other = Path.Combine(_root, "B.cs");
        await File.WriteAllLinesAsync(path, ["a", "b"], _ct);
        await File.WriteAllLinesAsync(other, ["a", "b"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(path, ["a", "B"], _ct);
        await File.WriteAllLinesAsync(other, ["a", "B"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "edit");

        // A.cs gains a line above the committed change, so line 2 of HEAD is line 3 on disk.
        await File.WriteAllLinesAsync(path, ["new", "a", "B"], _ct);
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 2, 2), [new(2, 1), new(3, 1)]);
        engine.FileCoverage[other] = new FileCoverageSnapshot(Summary(other, 1, 1), [new(2, 1)]);
        engine.CoveringTests[path] = [Covering("Lib.Tests.Pass", 1, 3)];
        engine.CoveringTests[other] = [Covering("Lib.Tests.Pass", 1, 2)];
        engine.State.TestSuites =
        [
            new TestSuite("Lib.Tests", [Result("Lib.Tests.Pass", TestStatus.Passed)], DateTimeOffset.UnixEpoch, TimeSpan.Zero),
        ];
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.GetChangedLineCoverageAsync("HEAD~1..HEAD", "range", _ct))!;

        var files = json["files"]!.AsArray();
        Assert.Equal(["A.cs", "B.cs"], files.Select(f => f!["path"]!.GetValue<string>()));
        Assert.True(files[0]!["isStale"]!.GetValue<bool>());
        Assert.Equal("stale", Assert.Single(files[0]!["unknown"]!.AsArray())!["reason"]!.GetValue<string>());
        Assert.Equal("2", files[1]!["coveredByPassing"]!.GetValue<string>());
        Assert.Contains(json["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("differ between", StringComparison.Ordinal));
    }

    private static CoveringTest Covering(string fqn, int start, int end, bool current = true) =>
        new(fqn, "/tests/Lib.Tests/Lib.Tests.csproj", "test", [new CoveredLineRange(start, end)], current);

    private static TestResult Result(string fqn, TestStatus status) =>
        new(fqn, fqn, status, TimeSpan.Zero, null, null, null, null);

    private static TestSuite LibSuite(TestStatus status) =>
        new("Lib.Tests", [Result("Lib.Tests.T", status)], DateTimeOffset.UnixEpoch, TimeSpan.Zero);

    private static TestRunRecord Running(string runId) => new()
    {
        RunId = runId,
        EngineInstanceId = "engine",
        Sequence = 1,
        Trigger = TestRunTrigger.FileChange,
        Status = TestRunStatus.Running,
        RequestedAt = DateTimeOffset.UnixEpoch,
    };

    /// <summary>A working-tree change to lines 2 and 3 of a measured file: line 2 ran, line 3 did not.</summary>
    private async Task<(StubEngine Engine, string Path)> ChangedMeasuredFileAsync()
    {
        Git("init", "-q", "-b", "main");
        var path = Path.Combine(_root, "Code.cs");
        await File.WriteAllLinesAsync(path, ["a", "b", "c"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(path, ["a", "B", "C"], _ct);

        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 2, 1), [new(2, 1), new(3, 0)]);
        engine.CoveringTests[path] = [Covering("Lib.Tests.T", 1, 3)];
        engine.State.TestSuites = [LibSuite(TestStatus.Passed)];
        return (engine, path);
    }

    [Fact]
    public async Task GetChangedLineVerdicts_RunPublishedDuringTheRead_RereadsBothFromTheSameRun()
    {
        var (engine, _) = await ChangedMeasuredFileAsync();
        var published = false;
        engine.OnGetFileCoverage = _ =>
        {
            if (published)
            {
                return;
            }

            // A run where the test fails stores its coverage, then its results, after the outcomes were captured.
            published = true;
            engine.State.BeginResultsUpdate();
            engine.State.TestSuites = [LibSuite(TestStatus.Failed)];
            engine.State.EndResultsUpdate();
        };

        var result = await ChangedLineVerdicts.GetAsync(engine, _root, null, null, _ct);

        var lines = Assert.Single(result.Files).Lines;
        Assert.Equal(ChangedLineVerdict.CoveredOnlyByFailing, lines[0].Verdict);
        Assert.Equal(ChangedLineVerdict.Uncovered, lines[1].Verdict);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task GetChangedLineVerdicts_RunStillPublishing_ReportsEveryLineAsResultsUpdating()
    {
        var (engine, _) = await ChangedMeasuredFileAsync();
        engine.State.CurrentRun = Running("run-1");
        engine.OnGetFileCoverage = _ =>
        {
            if (!engine.State.IsPublishingResults)
            {
                engine.State.BeginResultsUpdate();
            }
        };

        var result = await ChangedLineVerdicts.GetAsync(engine, _root, null, null, _ct, settleTimeout: TimeSpan.FromMilliseconds(100));

        var lines = Assert.Single(result.Files).Lines;
        Assert.All(lines, l => Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.ResultsUpdating), (l.Verdict, l.Reason)));
        Assert.Equal(2, result.UnknownReasons[UnknownReason.ResultsUpdating]);
        Assert.Contains(result.Warnings, w => w.Contains("resultsUpdating", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, w => w.Contains("run-1", StringComparison.Ordinal));
    }

    /// <summary>
    /// Cancels once the coverage read is blocked, not on a timer: a timer can fire while git still runs in the
    /// repository, and the killed git can still hold it when the test deletes it.
    /// </summary>
    internal static void CancelAndBlock(CancellationTokenSource cts, ManualResetEventSlim release)
    {
        _ = Task.Run(cts.Cancel);
        _ = release.Wait(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GetChangedLineCoverage_RequestedWhileARunHoldsTheCoverageLock_IsCancellable()
    {
        var (engine, _) = await ChangedMeasuredFileAsync();
        using var release = new ManualResetEventSlim();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        engine.OnGetCoverageReport = () =>
        {
            CancelAndBlock(cts, release);
            return engine.CoverageReport;
        };
        engine.OnGetFileCoverage = _ => CancelAndBlock(cts, release);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tools.GetChangedLineCoverageAsync(ct: cts.Token));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task GetChangedLineVerdicts_CoverageReadBlockedByARun_GivesUpWithoutWaitingForIt()
    {
        var (engine, _) = await ChangedMeasuredFileAsync();
        using var release = new ManualResetEventSlim();
        engine.OnGetFileCoverage = _ => release.Wait(TimeSpan.FromSeconds(30));
        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var result = await ChangedLineVerdicts.GetAsync(engine, _root, null, null, _ct, settleTimeout: TimeSpan.FromMilliseconds(200));

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Took {stopwatch.Elapsed}.");
            var lines = Assert.Single(result.Files).Lines;
            Assert.All(lines, l => Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.ResultsUpdating), (l.Verdict, l.Reason)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task GetChangedLineVerdicts_CoverageReadBlocked_HonoursCancellation()
    {
        var (engine, _) = await ChangedMeasuredFileAsync();
        using var release = new ManualResetEventSlim();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        engine.OnGetFileCoverage = _ => CancelAndBlock(cts, release);
        try
        {
            _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                ChangedLineVerdicts.GetAsync(engine, _root, null, null, cts.Token, settleTimeout: TimeSpan.FromSeconds(30)));
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task GetChangedLineVerdicts_FileEditedDuringTheRead_RereadsTheChangedLines()
    {
        var (engine, path) = await ChangedMeasuredFileAsync();
        var edited = false;
        engine.OnGetFileCoverage = _ =>
        {
            if (!edited)
            {
                // Line 3 is reverted after git read the changes, and the coverage now describes the new content.
                edited = true;
                File.WriteAllLines(path, ["a", "B", "c"]);
            }
        };

        var result = await ChangedLineVerdicts.GetAsync(engine, _root, null, null, _ct);

        var line = Assert.Single(Assert.Single(result.Files).Lines);
        Assert.Equal((2, ChangedLineVerdict.CoveredByPassing), (line.Line, line.Verdict));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public async Task GetChangedLineVerdicts_FileKeepsChanging_ReportsEveryLineAsStale()
    {
        var (engine, path) = await ChangedMeasuredFileAsync();
        var edits = 0;
        engine.OnGetFileCoverage = _ => File.WriteAllLines(path, ["a", "B", $"C{++edits}"]);

        var result = await ChangedLineVerdicts.GetAsync(engine, _root, null, null, _ct, settleTimeout: TimeSpan.FromMilliseconds(200));

        var file = Assert.Single(result.Files);
        Assert.True(file.IsStale);
        Assert.All(file.Lines, l => Assert.Equal((ChangedLineVerdict.Unknown, UnknownReason.Stale), (l.Verdict, l.Reason)));
        Assert.Contains(result.Warnings, w => w.Contains("kept changing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetChangedLineVerdicts_RunFinishingDuringTheRead_StillWarns()
    {
        var (engine, _) = await ChangedMeasuredFileAsync();
        engine.State.CurrentRun = Running("run-2");
        engine.OnGetFileCoverage = _ => engine.State.CurrentRun = null;

        var result = await ChangedLineVerdicts.GetAsync(engine, _root, null, null, _ct);

        Assert.Equal(ChangedLineVerdict.CoveredByPassing, Assert.Single(result.Files).Lines[0].Verdict);
        Assert.Contains(result.Warnings, w => w.Contains("run-2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetChangedLineCoverage_UsesRemappedLinesAndReportsStaleChangedLines()
    {
        Git("init", "-q", "-b", "main");
        var measured = Path.Combine(_root, "Measured.cs");
        await File.WriteAllLinesAsync(measured, ["a", "b", "c"], _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");
        await File.WriteAllLinesAsync(measured, ["a", "B", "C"], _ct);

        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage[measured] = new FileCoverageSnapshot(
            Summary(measured, 2, 1, stale: true),
            [new(2, 1)])
        {
            StaleLines = [new CoveredLineRange(3, 3)],
        };
        engine.CoveringTests[measured] = [Covering("Lib.Tests.T", 2, 2)];
        engine.State.TestSuites = [LibSuite(TestStatus.Passed)];
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.GetChangedLineCoverageAsync(ct: _ct))!;

        // Line 2 is in an unchanged member, so its remapped coverage still applies; line 3 changed after it was measured.
        var file = Assert.Single(json["files"]!.AsArray())!;
        Assert.True(file["isStale"]!.GetValue<bool>());
        Assert.Equal("2", file["coveredByPassing"]!.GetValue<string>());
        var unknown = Assert.Single(file["unknown"]!.AsArray())!;
        Assert.Equal(("3", "stale"), (unknown["lines"]!.GetValue<string>(), unknown["reason"]!.GetValue<string>()));
    }

    [Fact]
    public async Task GetChangedLineCoverage_RejectsOptionLikeBase()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        _ = await Assert.ThrowsAsync<McpException>(() => tools.GetChangedLineCoverageAsync("--output=/tmp/x", ct: _ct));
    }

    [Fact]
    public void ExportCoverage_WritesDefaultFileInsideSolution()
    {
        var engine = Engine();
        var path = Path.Combine(_root, "A.cs");
        engine.CoverageReport = new CoverageReportSnapshot([Summary(path, 1, 1)]);
        engine.FileCoverage[path] = new FileCoverageSnapshot(Summary(path, 1, 1), [new(1, 1)]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(tools.ExportCoverage("cobertura"))!;

        var written = Path.Combine(_root, ".piston", "coverage", "cobertura.xml");
        Assert.Equal(written, json["path"]!.GetValue<string>());
        Assert.Equal(1, json["files"]!.GetValue<int>());
        Assert.Contains("<coverage", File.ReadAllText(written), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.info")]
    [InlineData("/tmp/outside.info")]
    [InlineData(".")]
    public void ResolveOutputPath_RefusesPathsOutsideSolution(string outputPath) =>
        Assert.Throws<McpException>(() => CoverageTools.ResolveOutputPath(outputPath, _root, "lcov.info"));

    [Fact]
    public void ResolveOutputPath_ResolvesRelativeToSolution() =>
        Assert.Equal(
            Path.Combine(_root, "out", "lcov.info"),
            CoverageTools.ResolveOutputPath("out/lcov.info", _root, "lcov.info"));

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _root, RedirectStandardError = true };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    [Fact]
    public async Task PlanAffectedTests_WithFiles_ListsSelectedTestsWithReasons()
    {
        var engine = Engine();
        var lib = Path.Combine(_root, "src", "Lib", "Code.cs");
        var project = Path.Combine(_root, "tests", "Lib.Tests", "Lib.Tests.csproj");
        engine.AffectedPlan = new ImpactAnalysisResult([Path.Combine(_root, "src", "Lib", "Lib.csproj")], [project], false, false)
        {
            AffectedTestFqns = ["Lib.Tests.B.Two", "Lib.Tests.A.One"],
            SelectionReasons = new Dictionary<string, IReadOnlyList<TestSelectionReason>>
            {
                ["Lib.Tests.A.One"] = [new(lib, "12", "10-14", "class", project, Branches: [new(12, 1, 2)])],
            },
            Explanation = "Tests whose recorded execution touches the changed lines.",
        };
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.PlanAffectedTestsAsync(["src/Lib/Code.cs"], ct: _ct))!;

        Assert.Equal([lib], engine.PlannedFiles);
        Assert.Equal("piston.affected-plan/v1", (string?)json["schema"]);
        Assert.Equal("files", (string?)json["base"]);
        Assert.Equal("tests", (string?)json["scope"]);
        Assert.Equal(["src/Lib/Lib.csproj"], json["buildProjects"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["tests/Lib.Tests/Lib.Tests.csproj"], json["testProjects"]!.AsArray().Select(n => (string?)n));
        var tests = json["tests"]!.AsArray();
        Assert.Equal(["Lib.Tests.A.One", "Lib.Tests.B.Two"], tests.Select(t => (string?)t!["name"]));
        var reason = Assert.Single(tests[0]!["reasons"]!.AsArray())!;
        Assert.Equal("src/Lib/Code.cs", (string?)reason["file"]);
        Assert.Equal(("12", "10-14", "class"), ((string?)reason["changedLines"], (string?)reason["executedLines"], (string?)reason["granularity"]));
        var branch = Assert.Single(reason["branches"]!.AsArray())!;
        Assert.Equal((12, 1, 2), (
            branch["line"]!.GetValue<int>(), branch["taken"]!.GetValue<int>(), branch["total"]!.GetValue<int>()));
        Assert.Null(tests[1]!["reasons"]);
    }

    [Fact]
    public void AffectedPlan_MoreTestsThanARunNarrowsTo_IsProjectScope()
    {
        var project = Path.Combine(_root, "T.csproj");
        var plan = new ImpactAnalysisResult([], [project], false, false)
        {
            AffectedTestFqns = [.. Enumerable.Range(0, ImpactAnalysisResult.MaxFilteredTests + 1).Select(i => $"T.Test{i}")],
        };

        var json = CoverageTools.AffectedPlan("HEAD", [Path.Combine(_root, "A.cs")], plan, _root);

        Assert.Equal("projects", (string?)json["scope"]);
        Assert.Empty(json["tests"]!.AsArray());
        Assert.Contains("101 tests", (string?)json["explanation"], StringComparison.Ordinal);
    }

    [Fact]
    public void AffectedPlan_FullRun_IsFullScope()
    {
        var plan = new ImpactAnalysisResult([], [], false, IsFullRun: true) { Explanation = "A solution file changed." };

        var json = CoverageTools.AffectedPlan("files", [Path.Combine(_root, "App.slnx")], plan, _root);

        Assert.Equal("full", (string?)json["scope"]);
        Assert.Equal("A solution file changed.", (string?)json["explanation"]);
        Assert.Equal(["App.slnx"], json["changedFiles"]!.AsArray().Select(n => (string?)n));
    }
}
