// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class DurationsTests
{
    [Theory]
    [InlineData("90", 90_000)]
    [InlineData("500ms", 500)]
    [InlineData("30s", 30_000)]
    [InlineData("5m", 300_000)]
    [InlineData("1.5h", 5_400_000)]
    [InlineData(" 2 M ", 120_000)]
    public void Parses(string text, double expectedMs)
    {
        Assert.True(Durations.TryParse(text, out var value));
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("off")]
    [InlineData("OFF")]
    public void ZeroOrOffIsOff(string text)
    {
        Assert.True(Durations.TryParse(text, out var value));
        Assert.Null(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5s")]
    [InlineData("5d")]
    public void RejectsOtherText(string text) => Assert.False(Durations.TryParse(text, out _));

    [Theory]
    [InlineData(250, "250 ms")]
    [InlineData(30_000, "30 s")]
    [InlineData(1_500, "1.5 s")]
    [InlineData(300_000, "5 min")]
    [InlineData(5_400_000, "1.5 h")]
    public void Formats(double ms, string expected) => Assert.Equal(expected, Durations.Format(TimeSpan.FromMilliseconds(ms)));
}
