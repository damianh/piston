// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using Piston.Engine;
using Piston.Engine.Models;
using Piston.Mcp.Tools;
using Xunit;

namespace Piston.Tests.Mcp;

public sealed class AffectedChangesTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-affected-").FullName;

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(null, null, true, "Working")]
    [InlineData(null, "main", true, "Branch")]
    [InlineData("Branch", null, true, "Branch")]
    [InlineData("range", "a..b", true, "Range")]
    [InlineData("working", "main", true, "Working")]
    [InlineData("1", null, false, "Working")]
    [InlineData("staged", null, false, "Working")]
    public void TryParseBase_AcceptsTheThreeBases(string? text, string? reference, bool valid, string expected)
    {
        Assert.Equal(valid, ChangedLines.TryParseBase(text, reference, out var kind));
        Assert.Equal(Enum.Parse<ChangeBase>(expected), kind);
    }

    [Fact]
    public async Task Branch_IncludesCommittedUncommittedAndUntrackedChangesSinceTheBranchLeftItsBase()
    {
        var mainSha = await CreateFeatureBranchAsync();

        var changes = await ChangedLines.GetAsync(_root, ChangeBase.Branch, "main", _ct);

        Assert.Equal(mainSha, changes.Basis);
        Assert.Equal(["Committed.cs", "Uncommitted.cs", "Untracked.cs"], Names(changes));
        Assert.Equal([2], changes.Files[Path.Combine(_root, "Committed.cs")]);
        Assert.Empty(changes.Warnings);
    }

    [Fact]
    public async Task Branch_WithoutARef_UsesTheDefaultBranch()
    {
        var mainSha = await CreateFeatureBranchAsync();

        var changes = await ChangedLines.GetAsync(_root, ChangeBase.Branch, null, _ct);

        Assert.Equal(mainSha, changes.Basis);
    }

    [Fact]
    public async Task Branch_WithoutAMergeBase_ComparesWithTheRefAndWarns()
    {
        _ = await CreateFeatureBranchAsync();
        var orphan = GitOutput("commit-tree", GitOutput("rev-parse", "HEAD^{tree}").Trim(), "-m", "unrelated").Trim();
        Git("branch", "unrelated", orphan);

        var changes = await ChangedLines.GetAsync(_root, ChangeBase.Branch, "unrelated", _ct);

        Assert.Equal("unrelated", changes.Basis);
        Assert.Contains("no merge base", Assert.Single(changes.Warnings), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main..feature")]
    [InlineData("main...")]
    [InlineData("feature")]
    public async Task Range_ComparesCommitsOnly(string range)
    {
        _ = await CreateFeatureBranchAsync();

        var changes = await ChangedLines.GetAsync(_root, ChangeBase.Range, range, _ct);

        Assert.Equal(range, changes.Basis);
        Assert.Equal(["Committed.cs"], Names(changes));
    }

    [Fact]
    public async Task Range_OfARootCommit_ListsItsFiles()
    {
        _ = await CreateFeatureBranchAsync();

        var changes = await ChangedLines.GetAsync(_root, ChangeBase.Range, "main", _ct);

        Assert.Equal(["Committed.cs", "Uncommitted.cs"], Names(changes));
    }

    [Theory]
    [InlineData("Range", null)]
    [InlineData("Range", "main..--output=x")]
    [InlineData("Range", "nope..main")]
    [InlineData("Branch", "nope")]
    public async Task UnknownOrUnsafeRefsFail(string kind, string? reference)
    {
        _ = await CreateFeatureBranchAsync();

        _ = await Assert.ThrowsAsync<InvalidOperationException>(() => ChangedLines.GetAsync(_root, Enum.Parse<ChangeBase>(kind), reference, _ct));
    }

    [Fact]
    public async Task RunAffectedTests_RunsTheNarrowedSelectionAndReportsTheRun()
    {
        var engine = Engine();
        var project = Path.Combine(_root, "tests", "Lib.Tests", "Lib.Tests.csproj");
        engine.AffectedPlan = new ImpactAnalysisResult([], [project], false, false)
        {
            AffectedTestFqns = ["Lib.Tests.B.Two", "Lib.Tests.A.One"],
        };
        engine.RunSelectedRun = Task.FromResult(new TestRunRecord
        {
            RunId = "run-3",
            EngineInstanceId = "engine",
            Sequence = 3,
            Trigger = TestRunTrigger.Targeted,
            Status = TestRunStatus.Completed,
            RequestedAt = DateTimeOffset.UnixEpoch,
        });
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.RunAffectedTestsAsync(["src/Lib/Code.cs"], ct: _ct))!;

        Assert.Equal(["Lib.Tests"], engine.LastSelection!.Projects);
        Assert.Equal(["Lib.Tests.A.One", "Lib.Tests.B.Two"], engine.LastSelection.Tests);
        Assert.Equal("tests", (string?)json["scope"]);
        Assert.Equal("run-3", (string?)json["run"]!["runId"]);
        Assert.Equal("Completed", (string?)json["run"]!["status"]);
    }

    [Fact]
    public async Task RunAffectedTests_WhenNothingIsAffected_RunsNothing()
    {
        var engine = Engine();
        engine.AffectedPlan = new ImpactAnalysisResult([], [], false, false) { Explanation = "Only docs changed." };
        var tools = new CoverageTools(engine, NullMcpCallRecorder.Instance);

        var json = JsonNode.Parse(await tools.RunAffectedTestsAsync(["README.md"], ct: _ct))!;

        Assert.Equal("none", (string?)json["scope"]);
        Assert.Null(json["run"]);
        Assert.Null(engine.LastSelection);
    }

    [Fact]
    public async Task RunAffectedTests_RejectsAnUnknownBase()
    {
        var tools = new CoverageTools(Engine(), NullMcpCallRecorder.Instance);

        _ = await Assert.ThrowsAsync<McpException>(() => tools.RunAffectedTestsAsync(@base: "staged", ct: _ct));
    }

    private StubEngine Engine()
    {
        var engine = new StubEngine();
        engine.State.SolutionPath = Path.Combine(_root, "App.slnx");
        return engine;
    }

    /// <summary>main has Committed.cs and Uncommitted.cs; feature changes Committed.cs's second line, edits Uncommitted.cs
    /// without committing and adds Untracked.cs. Returns main's commit.</summary>
    private async Task<string> CreateFeatureBranchAsync()
    {
        Git("init", "-q", "-b", "main");
        await File.WriteAllTextAsync(Path.Combine(_root, "Committed.cs"), "a\nb\n", _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "Uncommitted.cs"), "a\n", _ct);
        Git("add", ".");
        Commit();
        var mainSha = GitOutput("rev-parse", "HEAD").Trim();
        Git("checkout", "-q", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(_root, "Committed.cs"), "a\nB\n", _ct);
        Git("commit", "-q", "-a", "-m", "change");
        await File.WriteAllTextAsync(Path.Combine(_root, "Uncommitted.cs"), "A\n", _ct);
        await File.WriteAllTextAsync(Path.Combine(_root, "Untracked.cs"), "u\n", _ct);
        return mainSha;
    }

    private void Commit() => Git("commit", "-q", "-m", "commit");

    private List<string> Names(ChangedLinesResult changes) =>
        [.. changes.Files.Keys.Select(f => Path.GetRelativePath(_root, f)).Order(StringComparer.Ordinal)];

    private void Git(params string[] args) => _ = GitOutput(args);

    private string GitOutput(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in (string[])["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", .. args])
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
