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
    public void GetCoverage_UnmeasuredFile_Fails()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        _ = Assert.Throws<McpException>(() => tools.GetCoverage("src/Nope.cs"));
    }

    [Fact]
    public async Task GetChangedLineCoverage_ClassifiesChangedExecutableLines()
    {
        Git("init", "-q", "-b", "main");
        var measured = Path.Combine(_root, "Measured.cs");
        var stale = Path.Combine(_root, "Stale.cs");
        await File.WriteAllLinesAsync(measured, ["a", "b", "c", "d", "e"], _ct);
        await File.WriteAllLinesAsync(stale, ["a"], _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "x", _ct);
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "init");

        await File.WriteAllLinesAsync(measured, ["a", "B", "C", "d", "E", "f"], _ct);
        await File.WriteAllLinesAsync(stale, ["A"], _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "README.md"), "y", _ct);
        await File.WriteAllLinesAsync(Path.Combine(_root, "New.cs"), ["n"], _ct);

        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        engine.FileCoverage[measured] = new FileCoverageSnapshot(
            Summary(measured, 4, 2),
            [new(2, 1), new(3, 0), new(4, 1), new(5, 1, 1, 2)]);
        engine.FileCoverage[stale] = new FileCoverageSnapshot(Summary(stale, 1, 1, stale: true), [new(1, 1)]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.GetChangedLineCoverageAsync(ct: _ct))!;

        Assert.Equal("piston.changed-line-coverage/v1", json["schema"]!.GetValue<string>());
        Assert.Equal(3, json["totals"]!["executable"]!.GetValue<int>());
        Assert.Equal(1, json["totals"]!["covered"]!.GetValue<int>());
        Assert.Equal(1, json["totals"]!["partial"]!.GetValue<int>());
        Assert.Equal(1, json["totals"]!["uncovered"]!.GetValue<int>());
        var file = Assert.Single(json["files"]!.AsArray())!;
        Assert.Equal("Measured.cs", file["path"]!.GetValue<string>());
        Assert.Equal("3", file["uncoveredLines"]!.GetValue<string>());
        Assert.Equal(["Stale.cs"], json["staleFiles"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.Equal(["New.cs"], json["notMeasuredFiles"]!.AsArray().Select(f => f!.GetValue<string>()));
    }

    [Fact]
    public async Task GetChangedLineCoverage_RejectsOptionLikeBase()
    {
        var engine = Engine();
        engine.CoverageReport = new CoverageReportSnapshot([]);
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        _ = await Assert.ThrowsAsync<McpException>(() => tools.GetChangedLineCoverageAsync("--output=/tmp/x", _ct));
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
                ["Lib.Tests.A.One"] = [new(lib, "12", "10-14", "class", project)],
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
