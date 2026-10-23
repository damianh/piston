// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class SnapshotDiffTests
{
    private static string[] Lines(string text) => SnapshotDiff.SplitLines(text);

    [Fact]
    public void Compute_IdenticalContent_ReturnsNull() =>
        Assert.Null(SnapshotDiff.Compute(Lines("a\nb\nc"), Lines("a\nb\nc")));

    [Fact]
    public void Compute_ReplacedLine_ReportsItsRange()
    {
        var diff = SnapshotDiff.Compute(Lines("a\nb\nc\nd"), Lines("a\nB\nC\nd"))!.Value;

        Assert.Equal(new SnapshotDiff(1, 2, 2), diff);
        Assert.Equal(new LineSpan(2, 3), diff.OldRange);
    }

    [Fact]
    public void Compute_Insertion_TouchesTheLinesEitherSide()
    {
        var diff = SnapshotDiff.Compute(Lines("a\nb\nc"), Lines("a\nb\nx\ny\nc"))!.Value;

        Assert.Equal(new SnapshotDiff(2, 0, 2), diff);
        Assert.Equal(new LineSpan(2, 3), diff.OldRange);
    }

    [Fact]
    public void Compute_InsertionAtStart_StartsAtLineOne()
    {
        var diff = SnapshotDiff.Compute(Lines("a\nb"), Lines("x\na\nb"))!.Value;

        Assert.Equal(new LineSpan(1, 1), diff.OldRange);
    }

    [Fact]
    public void Compute_Deletion_ReportsDeletedLines()
    {
        var diff = SnapshotDiff.Compute(Lines("a\nb\nc\nd"), Lines("a\nd"))!.Value;

        Assert.Equal(new SnapshotDiff(1, 2, 0), diff);
        Assert.Equal(new LineSpan(2, 3), diff.OldRange);
    }

    [Fact]
    public void Compute_RepeatedLines_DoesNotOverlapPrefixAndSuffix()
    {
        var diff = SnapshotDiff.Compute(Lines("a\na"), Lines("a\na\na"))!.Value;

        Assert.Equal(new SnapshotDiff(2, 0, 1), diff);
    }

    [Fact]
    public void ComputeCodeRange_CommentsAndWhitespaceOnly_ReturnsNull()
    {
        const string original = "class C\n{\n    void M() { int x = 1; }\n}";
        const string edited = "class C\n{\n    // a comment\n\n    void M(){int x=1;}\n}";

        Assert.Null(SnapshotDiff.ComputeCodeRange(original, edited));
    }

    [Fact]
    public void ComputeCodeRange_CodeChange_ReturnsOldLineRange()
    {
        const string original = "class C\n{\n    void M() { int x = 1; }\n}";
        const string edited = "class C\n{\n    void M() { int x = 2; }\n}";

        Assert.Equal(new LineSpan(3, 3), SnapshotDiff.ComputeCodeRange(original, edited));
    }

    [Fact]
    public void ComputeCodeRange_DirectiveChangesAreSignificant()
    {
        const string original = "#if DEBUG\nclass C {}\n#endif";
        const string edited = "#if RELEASE\nclass C {}\n#endif";

        Assert.True(SnapshotDiff.ComputeCodeRange(original, edited) is not null);
    }

    [Fact]
    public void Remap_KeepsSpansBeforeAndShiftsSpansAfterTheChange()
    {
        var diff = new SnapshotDiff(Prefix: 10, OldCount: 2, NewCount: 5);

        var spans = diff.Remap([new(1, 5), new(20, 25)]);

        Assert.Equal([new(1, 5), new(23, 28)], spans);
    }

    [Fact]
    public void Remap_SpanContainingTheChange_GrowsToCoverTheNewLines()
    {
        var diff = new SnapshotDiff(Prefix: 10, OldCount: 2, NewCount: 5);

        var spans = diff.Remap([new(8, 14)]);

        Assert.Equal([new(8, 17)], spans);
    }

    [Fact]
    public void Remap_SpanStraddlingAnInsertion_CoversTheInsertedLines()
    {
        var diff = new SnapshotDiff(Prefix: 5, OldCount: 0, NewCount: 3);

        var spans = diff.Remap([new(3, 7)]);

        Assert.Equal([new(3, 10)], spans);
    }

    [Fact]
    public void Remap_SpanWhollyDeleted_KeepsTheJoinLine()
    {
        var diff = new SnapshotDiff(Prefix: 4, OldCount: 3, NewCount: 0);

        var spans = diff.Remap([new(5, 7), new(10, 12)]);

        Assert.Equal([new(5, 5), new(7, 9)], spans);
    }
}
