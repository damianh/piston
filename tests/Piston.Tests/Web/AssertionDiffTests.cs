// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public class AssertionDiffTests
{
    [Fact]
    public void Parse_MsTestObjectForm()
    {
        var diff = AssertionDiff.Parse("Assert.AreEqual failed. Expected:<10>. Actual:<9>. Totals differ");

        Assert.Equal(new AssertionDiff("10", "9", "Assert.AreEqual failed. Totals differ"), diff);
    }

    [Fact]
    public void Parse_XunitStrings_DropsMarkerLines()
    {
        const string message = """
            Assert.Equal() Failure: Strings differ
                       ↓ (pos 2)
            Expected: "abc"
            Actual:   "abd"
                       ↑ (pos 2)
            """;

        var diff = AssertionDiff.Parse(message);

        Assert.Equal(new AssertionDiff("abc", "abd", "Assert.Equal() Failure: Strings differ"), diff);
    }

    [Fact]
    public void Parse_RepeatedEmptyLabels_AreDroppedFromSummary()
    {
        var diff = AssertionDiff.Parse("Assert.Equal() Failure: Values differ\nExpected: Faulted\nActual:   NotFullScope\nExpected: \nActual:   ");

        Assert.Equal(new AssertionDiff("Faulted", "NotFullScope", "Assert.Equal() Failure: Values differ"), diff);
    }

    [Fact]
    public void Parse_NUnitButWas_DropsCaretLine()
    {
        var diff = AssertionDiff.Parse("  Expected: 5\r\n  But was:  4\r\n  -----^\r\n");

        Assert.Equal(new AssertionDiff("5", "4", ""), diff);
    }

    [Fact]
    public void Parse_Shouldly()
    {
        var diff = AssertionDiff.Parse("total\n    should be\n5\n    but was\n4");

        Assert.Equal(new AssertionDiff("5", "4", "total"), diff);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("System.NullReferenceException: Object reference not set to an instance of an object.")]
    [InlineData("Expected: 5")]
    [InlineData("Build input is outside the source fingerprint.\nExpected: \nActual:   ")]
    public void Parse_Unrecognised_ReturnsNull(string? message) => Assert.Null(AssertionDiff.Parse(message));

    [Theory]
    [InlineData("abcXdef", "abcYdef", "abc", "X", "Y", "def")]
    [InlineData("same", "same", "same", "", "", "")]
    [InlineData("abc", "abcd", "abc", "", "d", "")]
    [InlineData("aa", "aaa", "aa", "", "a", "")]
    [InlineData("", "x", "", "", "x", "")]
    public void Split_FindsCommonPrefixAndSuffix(
        string expected, string actual, string prefix, string expectedMiddle, string actualMiddle, string suffix)
    {
        var split = new AssertionDiff(expected, actual, "").Split();

        Assert.Equal((prefix, expectedMiddle, actualMiddle, suffix), split);
        Assert.Equal(expected, split.Prefix + split.ExpectedMiddle + split.Suffix);
        Assert.Equal(actual, split.Prefix + split.ActualMiddle + split.Suffix);
    }
}
