// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Xunit;

namespace Piston.Engine.Tests.Impact;

public sealed class LineSpanTests
{
    [Fact]
    public void Normalize_SortsAndMergesOverlappingAndAdjacentSpans()
    {
        var spans = LineSpan.Normalize([new(10, 12), new(1, 3), new(4, 5), new(11, 15), new(20, 20)]);

        Assert.Equal([new(1, 5), new(10, 15), new(20, 20)], spans);
    }

    [Fact]
    public void FormatAndParse_RoundTrip()
    {
        IReadOnlyList<LineSpan> spans = [new(3, 9), new(12, 12)];

        var text = LineSpan.Format(spans);

        Assert.Equal("3-9,12", text);
        Assert.Equal(spans, LineSpan.Parse(text));
    }

    [Fact]
    public void Parse_EmptyOrNull_ReturnsNoSpans()
    {
        Assert.Empty(LineSpan.Parse(null));
        Assert.Empty(LineSpan.Parse(""));
    }

    [Theory]
    [InlineData(1, 3, 3, 5, true)]
    [InlineData(1, 3, 4, 5, false)]
    [InlineData(4, 4, 1, 9, true)]
    public void Overlaps_ComparesInclusiveRanges(int s1, int e1, int s2, int e2, bool expected) =>
        Assert.Equal(expected, new LineSpan(s1, e1).Overlaps(new LineSpan(s2, e2)));
}
