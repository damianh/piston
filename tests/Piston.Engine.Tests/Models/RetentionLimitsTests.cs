// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Models;

public sealed class RetentionLimitsTests
{
    [Fact]
    public void TruncateHead_LeavesShortTextUnchanged_AndMarksLongText()
    {
        const int limit = RetentionLimits.MaxTestTextLength;
        var shortText = new string('s', limit);
        var longText = new string('x', limit + 100);

        Assert.Same(shortText, RetentionLimits.TruncateHead(shortText, limit));
        var truncated = RetentionLimits.TruncateHead(longText, limit)!;
        Assert.InRange(truncated.Length, 1, limit);
        Assert.StartsWith(new string('x', 100), truncated, StringComparison.Ordinal);
        Assert.Contains("characters truncated", truncated, StringComparison.Ordinal);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncateHeadTail_KeepsBothEndsWithinLimit()
    {
        const int limit = RetentionLimits.MaxTestTextLength;
        var original = new string('a', limit) + new string('z', limit);

        var truncated = RetentionLimits.TruncateHeadTail(original, limit)!;

        Assert.InRange(truncated.Length, 1, limit);
        Assert.StartsWith(new string('a', 100), truncated, StringComparison.Ordinal);
        Assert.EndsWith(new string('z', 100), truncated, StringComparison.Ordinal);
        Assert.Contains("characters truncated", truncated, StringComparison.Ordinal);
    }
}
