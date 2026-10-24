// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class StackTraceParserTests
{
    private const string Trace =
        "   at Xunit.Assert.Equal[T](T expected, T actual) in /_/src/xunit.assert/Asserts/EqualityAsserts.cs:line 102\r\n" +
        "   at App.Tests.CalcTests.Adds() in /repo/tests/App.Tests/CalcTests.cs:line 17\n" +
        "   at System.RuntimeMethodHandle.InvokeMethod(Object target)\n" +
        "--- End of stack trace from previous location ---\n" +
        "   at App.Calc.Add(Int32 a) in C:\\repo\\src\\App\\Calc.cs:line 5";

    [Fact]
    public void Parse_FindsTheSourceLocationOfEachFrame()
    {
        var frames = StackTraceParser.Parse(Trace);

        Assert.Equal(5, frames.Count);
        Assert.Equal(("/_/src/xunit.assert/Asserts/EqualityAsserts.cs", 102), (frames[0].FilePath, frames[0].Line));
        Assert.Equal(("/repo/tests/App.Tests/CalcTests.cs", 17), (frames[1].FilePath, frames[1].Line));
        Assert.False(frames[2].HasSource);
        Assert.Equal("--- End of stack trace from previous location ---", frames[3].Text);
        Assert.Equal(("C:\\repo\\src\\App\\Calc.cs", 5), (frames[4].FilePath, frames[4].Line));
        Assert.DoesNotContain(frames, f => f.Text.EndsWith('\r'));
    }

    [Fact]
    public void FirstInSolution_SkipsFrameworkFrames()
    {
        var frames = StackTraceParser.Parse(Trace);

        Assert.Equal(17, StackTraceParser.FirstInSolution(frames, "/repo")!.Line);
        Assert.Equal(5, StackTraceParser.FirstInSolution(frames, "C:\\repo\\")!.Line);
        Assert.Null(StackTraceParser.FirstInSolution(frames, "/rep"));
        Assert.Null(StackTraceParser.FirstInSolution(frames, null));
    }

    [Fact]
    public void Parse_AcceptsTheMtpLocationWithoutTheLineKeyword()
    {
        var frames = StackTraceParser.Parse(
            "at App.Tests.CalcTests.Adds() in /repo/tests/CalcTests.cs:12\n" +
            "at App.Calc.Add() in C:\\repo\\Calc.cs:7");

        Assert.Equal(("/repo/tests/CalcTests.cs", 12), (frames[0].FilePath, frames[0].Line));
        Assert.Equal(("C:\\repo\\Calc.cs", 7), (frames[1].FilePath, frames[1].Line));
    }

    [Fact]
    public void Parse_EmptyTrace_HasNoFrames() => Assert.Empty(StackTraceParser.Parse(null));

    [Theory]
    [InlineData("/work/repo", "/work/REPO/Dependency.cs", "/work/repo/Tests.cs")]
    [InlineData("/work/repo", "/work/repository/Dependency.cs", "/work/repo/Tests.cs")]
    public void FirstInSolution_SkipsCaseDistinctUnixSiblings(string root, string sibling, string testFile)
    {
        var frames = StackTraceParser.Parse(
            $"at Dependency.Call() in {sibling}:line 2\nat Tests.Fails() in {testFile}:line 12");

        Assert.False(StackTraceParser.IsInSolution(sibling, root));
        Assert.Equal(testFile, StackTraceParser.FirstInSolution(frames, root)!.FilePath);
    }

    [Theory]
    [InlineData(@"C:\work\Repo", @"c:\WORK\REPO\Tests.cs")]
    [InlineData("C:/work/Repo", "c:/WORK/REPO/Tests.cs")]
    [InlineData(@"\\server\share\Repo", @"\\SERVER\SHARE\repo\Tests.cs")]
    [InlineData("//server/share/Repo", "//SERVER/SHARE/repo/Tests.cs")]
    [InlineData(@"C:\work\Repo", "c:/WORK/REPO/Tests.cs")]
    [InlineData("C:/work/Repo", @"c:\WORK\REPO\Tests.cs")]
    [InlineData(@"\\server\share\Repo", "//SERVER/SHARE/repo/Tests.cs")]
    [InlineData("//server/share/Repo", @"\\SERVER\SHARE\repo\Tests.cs")]
    [InlineData(@"C:\work/Repo", @"c:/WORK\REPO/Tests.cs")]
    public void WindowsDaemonPaths_AreCaseInsensitiveOnAnyBrowserHost(string root, string file) =>
        Assert.True(StackTraceParser.IsInSolution(file, root));

    [Theory]
    [InlineData(@"C:\work\Repo", "C:/work/Repository/Tests.cs")]
    [InlineData("//server/share/Repo", @"\\server\share\Repository\Tests.cs")]
    [InlineData("/work/repo", "/work\\repo/Tests.cs")]
    public void PathMatching_DoesNotAdmitSiblingsOrRewriteUnixSeparators(string root, string file) =>
        Assert.False(StackTraceParser.IsInSolution(file, root));
}
