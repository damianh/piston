// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;
using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class ImpactMapStoreTests : IAsyncLifetime
{
    private const string Project = "/repo/tests/Lib.Tests/Lib.Tests.csproj";
    private const string File = "/repo/src/Lib/Code.cs";

    private static readonly string Original = Source(30);

    private string _tempDir = string.Empty;
    private SqliteCoverageStore _sut = null!;

    public async ValueTask InitializeAsync()
    {
        _tempDir = Directory.CreateTempSubdirectory("piston-impact-store-test-").FullName;
        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_tempDir);
    }

    public ValueTask DisposeAsync()
    {
        _sut.Dispose();
        Directory.Delete(_tempDir, recursive: true);
        return ValueTask.CompletedTask;
    }

    private static string Source(int lines) => string.Join("\n", Enumerable.Range(1, lines).Select(i => $"int line{i} = {i};"));

    private static string Edit(string content, int line, string replacement)
    {
        var lines = SnapshotDiff.SplitLines(content);
        var identifier = string.Concat(replacement.Where(char.IsLetterOrDigit));
        lines[line - 1] = $"int changed_{identifier} = {line};";
        return string.Join("\n", lines);
    }

    private Task Record(
        ImpactGranularity granularity,
        string unitKey,
        string[] tests,
        LineSpan[] spans,
        string? content = null,
        bool merge = false,
        bool methodsComplete = false) =>
        _sut.RecordImpactAsync(new ImpactRecording(
            Project, granularity, unitKey, _sut.CreateRunId(), tests, [new ImpactFile(File, spans, content ?? Original)], merge, methodsComplete));

    [Fact]
    public async Task SelectTests_ChangeInsideAnExecutedSpan_SelectsTheUnitsTestsWithTheSpan()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A", "T.B"], [new(3, 5), new(10, 12)]);

        var selection = _sut.SelectTests([new ChangedFile(File, Edit(Original, 4, "changed"))]);

        Assert.Equal(["T.A", "T.B"], selection.Tests.Keys.Order());
        var reason = Assert.Single(selection.Tests["T.A"]);
        Assert.Equal(new LineSpan(4, 4), reason.ChangedLines);
        Assert.Equal(new LineSpan(3, 5), reason.ExecutedSpan);
        Assert.Equal(ImpactGranularity.Run, reason.Granularity);
        Assert.Equal(Project, reason.ProjectPath);
        Assert.Empty(selection.UnmappedFiles);
    }

    [Fact]
    public async Task SelectTests_ChangeOutsideEveryExecutedSpan_FallsBackToEveryTestThatRanTheFile()
    {
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);
        await Record(ImpactGranularity.Class, "T.B", ["T.B.One"], [new(10, 12)]);

        var selection = _sut.SelectTests([new ChangedFile(File, Edit(Original, 20, "new member"))]);

        Assert.Equal(["T.A.One", "T.B.One"], selection.Tests.Keys.Order());
        Assert.All(selection.Tests.Values, r => Assert.Null(Assert.Single(r).ExecutedSpan));
    }

    [Fact]
    public async Task SelectTests_UnchangedContent_SelectsNothing()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(1, 30)]);

        var selection = _sut.SelectTests([new ChangedFile(File, Original)]);

        Assert.Empty(selection.Tests);
        Assert.Empty(selection.UnmappedFiles);
    }

    [Fact]
    public async Task SelectTests_CommentsAndWhitespaceOnly_MarksFileUnchangedWithoutSelectingTests()
    {
        const string original = "class C\n{\n    void M() { int x = 1; }\n}";
        const string edited = "class C\n{\n    // comment\n\n    void M(){int x=1;}\n}";
        var path = Path.Combine(_tempDir, "Code.cs");
        System.IO.File.WriteAllText(path, edited);
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project,
            ImpactGranularity.Test,
            "T.A.One",
            _sut.CreateRunId(),
            ["T.A.One"],
            [new ImpactFile(path, [new(3, 3)], original)],
            Merge: false));

        var selection = _sut.SelectTests([new ChangedFile(path, edited)]);

        Assert.Empty(selection.Tests);
        Assert.Empty(selection.UnmappedFiles);
        Assert.Equal([path], selection.UnchangedFiles);
        var executor = Assert.Single(_sut.GetTestsExecuting(path));
        Assert.True(executor.IsUnitCurrent);
        Assert.True(executor.IsContentCurrent);
        Assert.Equal([new LineSpan(5, 5)], executor.Spans);
    }

    [Fact]
    public async Task SelectTests_FileWithNoRecording_IsUnmapped()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(1, 30)]);

        var selection = _sut.SelectTests([new ChangedFile("/repo/src/Lib/Other.cs", "x")]);

        Assert.Empty(selection.Tests);
        Assert.Equal(["/repo/src/Lib/Other.cs"], selection.UnmappedFiles);
    }

    [Fact]
    public async Task SelectTests_DeletedFile_SelectsEveryTestThatRanIt()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(3, 5)]);

        var selection = _sut.SelectTests([new ChangedFile(File, null)]);

        var reason = Assert.Single(selection.Tests["T.A"]);
        Assert.Null(reason.ExecutedSpan);
    }

    [Fact]
    public async Task SelectTests_CurrentClassUnits_OverrideTheRunUnit()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A.One", "T.B.One"], [new(1, 30)]);
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);
        await Record(ImpactGranularity.Class, "T.B", ["T.B.One"], [new(10, 12)]);

        var selection = _sut.SelectTests([new ChangedFile(File, Edit(Original, 11, "changed"))]);

        var (fqn, reasons) = Assert.Single(selection.Tests);
        Assert.Equal("T.B.One", fqn);
        Assert.Equal(ImpactGranularity.Class, Assert.Single(reasons).Granularity);
    }

    [Fact]
    public async Task MarkChangedAsync_MakesClassUnitsDirtySoTheRunUnitDecides()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A.One", "T.B.One"], [new(1, 30)]);
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);
        var edited = new ChangedFile(File, Edit(Original, 11, "changed"));

        await _sut.MarkChangedAsync([edited]);

        Assert.False(Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Class)).IsCurrent);
        Assert.True(Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Run)).IsCurrent);
        Assert.Equal(["T.A.One", "T.B.One"], _sut.SelectTests([edited]).Tests.Keys.Order());
    }

    [Fact]
    public async Task MarkChangedAsync_InsertingMemberAboveExecutedMethod_DirtiesBindingEvidence()
    {
        const string original = "class C\n{\n    void A() { int x = 1; }\n}";
        const string inserted = "class C\n{\n    void Added() { }\n    void A() { int x = 1; }\n}";
        var member = Assert.Single(SourceMembers.Parse(original).Members);
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Class, "C", _sut.CreateRunId(), ["T.C.A"],
            [new ImpactFile(File, [member.Lines], original)], Merge: false));

        await _sut.MarkChangedAsync([new ChangedFile(File, inserted)]);

        Assert.False(Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Class)).IsCurrent);
    }

    [Fact]
    public async Task MarkChangedAsync_AddedOverloadCannotSuppressNewerRunEvidence()
    {
        const string original = "class C\n{\nint M(object value) => 1;\n}";
        const string added = "class C\n{\nint M(object value) => 1;\nint M(string value) => 2;\n}";
        const string edited = "class C\n{\nint M(object value) => 1;\nint M(string value) => 3;\n}";
        await Record(ImpactGranularity.Test, "T.A", ["T.A"], [new(3, 3)], original);

        await _sut.MarkChangedAsync([new ChangedFile(File, added)]);
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(4, 4)], added);

        Assert.False(Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Test)).IsCurrent);
        var reason = Assert.Single(_sut.SelectTests([new ChangedFile(File, edited)]).Tests["T.A"]);
        Assert.Equal(ImpactGranularity.Run, reason.Granularity);
    }

    [Fact]
    public async Task SelectTests_WildcardRunEvidenceIsSelectedAlongsideMemberHits()
    {
        const string original = "class C\n{\nvoid A() { int x = 1; }\nvoid B() { int y = 2; }\n}";
        const string partial = "class C\n{\nvoid A() { int x = 3; }\nvoid B() { int y = 2; }\n}";
        const string edited = "class C\n{\nvoid A() { int x = 3; }\nvoid B() { int y = 4; }\n}";
        await Record(ImpactGranularity.Run, "run", ["T.A", "T.B"], [new(3, 3)], original);
        await Record(ImpactGranularity.Run, "run", ["T.B"], [new(4, 4)], partial, merge: true);
        await Record(ImpactGranularity.Test, "T.B", ["T.B"], [new(4, 4)], partial);

        var selection = _sut.SelectTests([new ChangedFile(File, edited)]);

        Assert.Equal(["T.A", "T.B"], selection.Tests.Keys.Order());
        Assert.Equal(ImpactGranularity.Run, Assert.Single(selection.Tests["T.A"]).Granularity);
        Assert.Equal(ImpactGranularity.Test, Assert.Single(selection.Tests["T.B"]).Granularity);
    }

    [Fact]
    public async Task GetTestsExecuting_MovedExecutedMethodsDoNotCoverTheMethodBetweenThem()
    {
        const string original = "class C\n{\nvoid A() { }\nvoid B() { }\nvoid Cc() { }\n}";
        const string reordered = "class C\n{\nvoid A() { }\nvoid Cc() { }\nvoid B() { }\n}";
        var path = Path.Combine(_tempDir, "Methods.cs");
        System.IO.File.WriteAllText(path, reordered);
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Test, "T.A", _sut.CreateRunId(), ["T.A"],
            [new ImpactFile(path, [new(3, 4)], original)], Merge: false));

        var executor = Assert.Single(_sut.GetTestsExecuting(path));

        Assert.True(executor.IsContentCurrent);
        Assert.Equal([new LineSpan(3, 3), new LineSpan(5, 5)], executor.Spans);
    }

    [Fact]
    public async Task SelectTests_ReorderingInitializedEventsSelectsTheirTests()
    {
        const string original = "class C\n{\nevent Action A = Create(\"A\");\nevent Action B = Create(\"B\");\n}";
        const string reordered = "class C\n{\nevent Action B = Create(\"B\");\nevent Action A = Create(\"A\");\n}";
        await Record(ImpactGranularity.Test, "T.A", ["T.A"], [new(3, 3)], original);

        var selection = _sut.SelectTests([new ChangedFile(File, reordered)]);

        Assert.Equal(["T.A"], selection.Tests.Keys);
        Assert.Empty(selection.UnchangedFiles);
    }

    [Fact]
    public async Task SelectTests_MethodBodyEdit_SelectsItsRecordedTestAndReportsMember()
    {
        const string original = "class C\n{\n    void A() { int x = 1; }\n    void B() { int y = 2; }\n}";
        const string edited = "class C\n{\n    void A() { int x = 3; }\n    void B() { int y = 2; }\n}";
        var members = SourceMembers.Parse(original).Members.ToDictionary(member => member.Key, StringComparer.Ordinal);
        var methodA = members.Values.Single(member => member.Key.StartsWith("C.A`", StringComparison.Ordinal));
        var methodB = members.Values.Single(member => member.Key.StartsWith("C.B`", StringComparison.Ordinal));
        var path = Path.Combine(_tempDir, "Methods.cs");
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Test, "C.A", _sut.CreateRunId(), ["T.C.A"],
            [new ImpactFile(path, [methodA.Lines], original)], Merge: false));
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Test, "C.B", _sut.CreateRunId(), ["T.C.B"],
            [new ImpactFile(path, [methodB.Lines], original)], Merge: false));

        var selection = _sut.SelectTests([new ChangedFile(path, edited)]);

        Assert.Equal(["T.C.A"], selection.Tests.Keys);
        Assert.Equal(methodA.Key, Assert.Single(selection.Tests["T.C.A"]).Member);
    }

    [Fact]
    public async Task RecordImpactAsync_Replace_MakesTheUnitCurrentAndDropsOldTests()
    {
        await Record(ImpactGranularity.Class, "T.A", ["T.A.Old"], [new(3, 5)]);
        var edited = Edit(Original, 4, "changed");
        await _sut.MarkChangedAsync([new ChangedFile(File, edited)]);

        await Record(ImpactGranularity.Class, "T.A", ["T.A.New"], [new(3, 5)], edited);

        var unit = Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Class));
        Assert.True(unit.IsCurrent);
        var selection = _sut.SelectTests([new ChangedFile(File, Edit(edited, 4, "again"))]);
        Assert.Equal(["T.A.New"], selection.Tests.Keys);
    }

    [Fact]
    public async Task RecordImpactAsync_MergeAfterAnEdit_RemapsTheEarlierSpans()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(3, 5)]);
        var shifted = "new 1\nnew 2\n" + Original;

        await Record(ImpactGranularity.Run, "run", ["T.B"], [new(25, 26)], shifted, merge: true);

        var selection = _sut.SelectTests([new ChangedFile(File, Edit(shifted, 6, "changed"))]);
        Assert.Equal(["T.A", "T.B"], selection.Tests.Keys.Order());
        Assert.Equal(new LineSpan(5, 7), Assert.Single(selection.Tests["T.A"]).ExecutedSpan);
    }

    [Fact]
    public async Task RecordImpactAsync_MergeOnUnchangedTopLevelFilePreservesEarlierSpans()
    {
        var path = Path.Combine(_tempDir, "Program.cs");
        System.IO.File.WriteAllText(path, Original);
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Run, "run", _sut.CreateRunId(), ["T.A"],
            [new ImpactFile(path, [new(3, 5)], Original)], Merge: false));
        await _sut.RecordImpactAsync(new ImpactRecording(
            Project, ImpactGranularity.Run, "run", _sut.CreateRunId(), ["T.B"],
            [new ImpactFile(path, [new(10, 12)], Original)], Merge: true));

        var executors = _sut.GetTestsExecuting(path);

        Assert.Equal(["T.A", "T.B"], executors.Select(executor => executor.TestFqn));
        Assert.All(executors, executor =>
        {
            Assert.True(executor.IsContentCurrent);
            Assert.Equal([new LineSpan(3, 5), new LineSpan(10, 12)], executor.Spans);
        });
    }

    [Fact]
    public async Task RecordImpactAsync_MergeWithCompleteMethods_RemovesRenamedCasesOfThoseMethods()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A.Theory(at: 10:00)", "T.A.Other", "T.B.Theory(at: 10:00)"], [new(3, 5)]);

        await Record(ImpactGranularity.Run, "run", ["T.A.Theory(at: 10:05)"], [new(3, 5)], merge: true, methodsComplete: true);

        Assert.Equal(
            ["T.A.Other", "T.A.Theory(at: 10:05)", "T.B.Theory(at: 10:00)"],
            Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Run)).Tests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task RecordImpactAsync_MergeWithIncompleteMethods_KeepsEarlierCases()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A.Theory(at: 10:00)"], [new(3, 5)]);

        await Record(ImpactGranularity.Run, "run", ["T.A.Theory(at: 10:05)"], [new(3, 5)], merge: true);

        Assert.Equal(
            ["T.A.Theory(at: 10:00)", "T.A.Theory(at: 10:05)"],
            Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Run)).Tests.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task GetUnits_ListsEachUnitsTests()
    {
        await Record(ImpactGranularity.Class, "T.B", ["T.B.Two", "T.B.One"], [new(3, 5)]);
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);

        var units = _sut.GetUnits(Project, ImpactGranularity.Class).OrderBy(u => u.UnitKey, StringComparer.Ordinal).ToList();

        Assert.Equal(["T.A", "T.B"], units.Select(u => u.UnitKey));
        Assert.Equal(["T.A.One"], units[0].Tests);
        Assert.Equal(["T.B.One", "T.B.Two"], units[1].Tests);
    }

    [Fact]
    public async Task RemoveUnitsAsync_ForgetsOnlyTheNamedUnits()
    {
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);
        await Record(ImpactGranularity.Class, "T.B", ["T.B.One"], [new(3, 5)]);

        await _sut.RemoveUnitsAsync(Project, ImpactGranularity.Class, ["T.A"]);

        Assert.Equal("T.B", Assert.Single(_sut.GetUnits(Project, ImpactGranularity.Class)).UnitKey);
        Assert.Equal(["T.B.One"], _sut.SelectTests([new ChangedFile(File, Edit(Original, 4, "changed"))]).Tests.Keys);
    }

    [Fact]
    public async Task GetTestsExecuting_CurrentClassEvidence_OverridesTheRunUnit()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A.One", "T.B.One"], [new(1, 30)]);
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);

        var executors = _sut.GetTestsExecuting(File);

        Assert.Equal(["T.A.One", "T.B.One"], executors.Select(e => e.TestFqn));
        Assert.Equal(ImpactGranularity.Class, executors[0].Granularity);
        Assert.Equal([new LineSpan(3, 5)], executors[0].Spans);
        Assert.Equal(ImpactGranularity.Run, executors[1].Granularity);
        Assert.All(executors, e => Assert.True(e.IsUnitCurrent));
    }

    [Fact]
    public async Task GetTestsExecuting_DirtyClassEvidence_FallsBackToTheCurrentRunUnit()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A.One"], [new(1, 30)]);
        await Record(ImpactGranularity.Class, "T.A", ["T.A.One"], [new(3, 5)]);
        await _sut.MarkChangedAsync([new ChangedFile(File, Edit(Original, 11, "changed"))]);

        var executor = Assert.Single(_sut.GetTestsExecuting(File));

        Assert.Equal(ImpactGranularity.Run, executor.Granularity);
        Assert.True(executor.IsUnitCurrent);
    }

    [Fact]
    public async Task GetTestsExecuting_FileWithNoRecording_IsEmpty()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(1, 30)]);

        Assert.Empty(_sut.GetTestsExecuting("/repo/src/Lib/Other.cs"));
    }

    [Fact]
    public async Task Clear_RemovesTheImpactMap()
    {
        await Record(ImpactGranularity.Run, "run", ["T.A"], [new(1, 30)]);
        _sut.Dispose();

        SqliteCoverageStore.Clear(_tempDir);
        _sut = new SqliteCoverageStore();
        await _sut.InitializeAsync(_tempDir);

        Assert.Empty(_sut.GetUnits(Project, ImpactGranularity.Run));
        Assert.Equal([File], _sut.SelectTests([new ChangedFile(File, "x")]).UnmappedFiles);
    }
}
