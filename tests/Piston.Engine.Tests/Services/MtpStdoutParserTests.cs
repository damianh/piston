using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpStdoutParserTests
{
    // ── Single passed test ─────────────────────────────────────────────────────

    [Fact]
    public void SinglePassed_ReturnsPassedResult()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("passed Ns.Class.Method (7ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("Ns.Class.Method", result.FullyQualifiedName);
        Assert.Equal(TestStatus.Passed, result.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(7), result.Duration);
    }

    // ── Single failed test ─────────────────────────────────────────────────────

    [Fact]
    public void SingleFailed_ReturnsFailedResult()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("failed Ns.Class.FailingTest (12ms)");
        sut.ProcessLine("  from C:\\path\\to.dll (net10.0|x64)");
        sut.ProcessLine("  Expected true but got false");
        sut.ProcessLine("  at Ns.Class.FailingTest() in Tests.cs:line 42");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("Ns.Class.FailingTest", result.FullyQualifiedName);
        Assert.Equal(TestStatus.Failed, result.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(12), result.Duration);
        Assert.Equal("C:\\path\\to.dll", result.Source);
        Assert.Contains("Expected true", result.ErrorMessage);
        Assert.Contains("at Ns.Class.FailingTest", result.StackTrace);
    }

    // ── Skipped test ───────────────────────────────────────────────────────────

    [Fact]
    public void SingleSkipped_ReturnsSkippedResult()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("skipped Ns.Class.SkippedTest (0ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal(TestStatus.Skipped, result.Status);
    }

    // ── Duration variants ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("7ms",    7.0)]
    [InlineData("150ms",  150.0)]
    [InlineData("1.2s",   1200.0)]
    [InlineData("< 1ms",  0.5)]
    public void ParseDuration_VariousFormats(string input, double expectedMs)
    {
        var duration = MtpStdoutParser.ParseDuration(input);
        Assert.Equal(expectedMs, duration.TotalMilliseconds, precision: 1);
    }

    // ── Source line ────────────────────────────────────────────────────────────

    [Fact]
    public void SourceLine_PopulatesSource()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("passed Ns.Class.Method (5ms)");
        sut.ProcessLine("  from C:\\repos\\project\\tests\\Tests.dll (net10.0|x64)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("C:\\repos\\project\\tests\\Tests.dll", result.Source);
    }

    [Fact]
    public void NoSourceLine_SourceIsNull()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("passed Ns.Class.Method (5ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Null(result.Source);
    }

    // ── Multiple tests in sequence ─────────────────────────────────────────────

    [Fact]
    public void MultipleTests_EachReturnsSeperately()
    {
        var sut = new MtpStdoutParser();
        var results = new List<MtpParsedResult>();

        void Feed(string line) { if (sut.ProcessLine(line) is { } r) results.Add(r); }

        Feed("passed Ns.Class.Test1 (1ms)");
        Feed("  from a.dll (net10.0|x64)");
        Feed("passed Ns.Class.Test2 (2ms)");
        Feed("  from a.dll (net10.0|x64)");
        Feed("failed Ns.Class.Test3 (3ms)");

        if (sut.Flush() is { } last) results.Add(last);

        Assert.Equal(3, results.Count);
        Assert.Equal("Ns.Class.Test1", results[0].FullyQualifiedName);
        Assert.Equal("Ns.Class.Test2", results[1].FullyQualifiedName);
        Assert.Equal("Ns.Class.Test3", results[2].FullyQualifiedName);
    }

    // ── Second test returned when third header seen ────────────────────────────

    [Fact]
    public void ProcessLine_ReturnsPreviousResult_WhenNewHeaderSeen()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("passed Ns.Class.First (1ms)");
        var result = sut.ProcessLine("passed Ns.Class.Second (2ms)");

        Assert.NotNull(result);
        Assert.Equal("Ns.Class.First", result.FullyQualifiedName);
    }

    // ── Flush returns null when nothing pending ────────────────────────────────

    [Fact]
    public void Flush_WhenNothingPending_ReturnsNull()
    {
        var sut = new MtpStdoutParser();
        Assert.Null(sut.Flush());
    }

    // ── Empty / blank lines ignored ────────────────────────────────────────────

    [Fact]
    public void BlankLines_AreIgnored()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("");
        sut.ProcessLine("   ");
        sut.ProcessLine("passed Ns.Class.Method (7ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("Ns.Class.Method", result.FullyQualifiedName);
    }

    // ── Multi-line stack trace ─────────────────────────────────────────────────

    [Fact]
    public void FailedTest_MultiLineStackTrace_AllLinesCollected()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("failed Ns.Class.ComplexFail (30ms)");
        sut.ProcessLine("  from tests.dll (net10.0|x64)");
        sut.ProcessLine("  Assert.Equal() failure: expected 1 but got 2");
        sut.ProcessLine("  at Ns.Class.ComplexFail() in Tests.cs:line 10");
        sut.ProcessLine("  at Ns.Base.RunTest() in Base.cs:line 5");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Contains("Assert.Equal()", result.ErrorMessage);
        Assert.Contains("at Ns.Class.ComplexFail", result.StackTrace);
        Assert.Contains("at Ns.Base.RunTest", result.StackTrace);
    }

    // ── Test name with parentheses ─────────────────────────────────────────────

    [Fact]
    public void TestNameWithParens_ParsedCorrectly()
    {
        // Parametrised test names often include parentheses
        var sut = new MtpStdoutParser();
        sut.ProcessLine("passed Ns.Class.Method(42, \"hello\") (7ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        // FQN should include everything except the trailing duration-in-parens
        Assert.StartsWith("Ns.Class.Method", result.FullyQualifiedName);
        Assert.Equal(TestStatus.Passed, result.Status);
    }

    // ── Passed test has no error info ─────────────────────────────────────────

    [Fact]
    public void PassedTest_ErrorAndStackAreNull()
    {
        var sut = new MtpStdoutParser();
        sut.ProcessLine("passed Ns.Class.Method (7ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.StackTrace);
    }
}
