// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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
        _ = sut.ProcessLine("passed Ns.Class.Method (7ms)");
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
        _ = sut.ProcessLine("failed Ns.Class.FailingTest (12ms)");
        _ = sut.ProcessLine("  from C:\\path\\to.dll (net10.0|x64)");
        _ = sut.ProcessLine("  Expected true but got false");
        _ = sut.ProcessLine("  at Ns.Class.FailingTest() in Tests.cs:line 42");
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
        _ = sut.ProcessLine("skipped Ns.Class.SkippedTest (0ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal(TestStatus.Skipped, result.Status);
    }

    // ── Duration variants ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("7ms", 7.0)]
    [InlineData("150ms", 150.0)]
    [InlineData("1.2s", 1200.0)]
    [InlineData("< 1ms", 0.5)]
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
        _ = sut.ProcessLine("passed Ns.Class.Method (5ms)");
        _ = sut.ProcessLine("  from C:\\repos\\project\\tests\\Tests.dll (net10.0|x64)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("C:\\repos\\project\\tests\\Tests.dll", result.Source);
    }

    [Fact]
    public void NoSourceLine_SourceIsNull()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.Method (5ms)");
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

        void Feed(string line) { if (sut.ProcessLine(line) is { } r) { results.Add(r); } }

        Feed("passed Ns.Class.Test1 (1ms)");
        Feed("  from a.dll (net10.0|x64)");
        Feed("passed Ns.Class.Test2 (2ms)");
        Feed("  from a.dll (net10.0|x64)");
        Feed("failed Ns.Class.Test3 (3ms)");

        if (sut.Flush() is { } last)
        {
            results.Add(last);
        }

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
        _ = sut.ProcessLine("passed Ns.Class.First (1ms)");
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
        _ = sut.ProcessLine("");
        _ = sut.ProcessLine("   ");
        _ = sut.ProcessLine("passed Ns.Class.Method (7ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("Ns.Class.Method", result.FullyQualifiedName);
    }

    // ── Multi-line stack trace ─────────────────────────────────────────────────

    [Fact]
    public void FailedTest_MultiLineStackTrace_AllLinesCollected()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("failed Ns.Class.ComplexFail (30ms)");
        _ = sut.ProcessLine("  from tests.dll (net10.0|x64)");
        _ = sut.ProcessLine("  Assert.Equal() failure: expected 1 but got 2");
        _ = sut.ProcessLine("  at Ns.Class.ComplexFail() in Tests.cs:line 10");
        _ = sut.ProcessLine("  at Ns.Base.RunTest() in Base.cs:line 5");
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
        _ = sut.ProcessLine("passed Ns.Class.Method(42, \"hello\") (7ms)");
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
        _ = sut.ProcessLine("passed Ns.Class.Method (7ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Null(result.ErrorMessage);
        Assert.Null(result.StackTrace);
    }

    // ── Captured output ───────────────────────────────────────────────────────

    [Fact]
    public void PassedTest_StandardAndErrorOutput_Captured()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.Method (7ms)");
        _ = sut.ProcessLine("  from tests.dll (net10.0|x64)");
        _ = sut.ProcessLine("  Standard output");
        _ = sut.ProcessLine("    first line");
        _ = sut.ProcessLine("    ");
        _ = sut.ProcessLine("      indented line");
        _ = sut.ProcessLine("  Error output");
        _ = sut.ProcessLine("    to stderr");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal(
            string.Join(Environment.NewLine, "first line", "", "  indented line"),
            result.Output);
        Assert.Equal("to stderr", result.ErrorOutput);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void FailedTest_OutputKeptSeparateFromErrorInfo()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("failed Ns.Class.Fails (12ms)");
        _ = sut.ProcessLine("  Expected true but got false");
        _ = sut.ProcessLine("  at Ns.Class.Fails() in Tests.cs:line 42");
        _ = sut.ProcessLine("  Standard output");
        _ = sut.ProcessLine("    at the start of output");
        _ = sut.ProcessLine("    passed Fake.Name (1ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Equal("Expected true but got false", result.ErrorMessage);
        Assert.Equal("at Ns.Class.Fails() in Tests.cs:line 42", result.StackTrace);
        Assert.Equal(
            string.Join(Environment.NewLine, "at the start of output", "passed Fake.Name (1ms)"),
            result.Output);
    }

    [Fact]
    public void Output_EndsAtNextTestHeader()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.First (1ms)");
        _ = sut.ProcessLine("  Standard output");
        _ = sut.ProcessLine("    from first");
        var first = sut.ProcessLine("passed Ns.Class.Second (1ms)");
        var second = sut.Flush();

        Assert.Equal("from first", first?.Output);
        Assert.NotNull(second);
        Assert.Null(second.Output);
    }

    [Fact]
    public void Output_EndsAtLessIndentedLine()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.Method (1ms)");
        _ = sut.ProcessLine("  Standard output");
        _ = sut.ProcessLine("    captured");
        _ = sut.ProcessLine("Test run summary: Passed!");
        var result = sut.Flush();

        Assert.Equal("captured", result?.Output);
    }

    [Fact]
    public void ArtifactSummary_FlushesLastTestAndAssociatesItsAttachment()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.Captures (1ms)");
        var completed = sut.ProcessLine("  1 file artifacts produced:");
        _ = sut.ProcessLine("- For test 'Ns.Class.Captures': /tmp/report with spaces.txt");

        Assert.Equal("Ns.Class.Captures", completed?.FullyQualifiedName);
        var attachment = Assert.Single(sut.Attachments);
        Assert.Equal("Ns.Class.Captures", attachment.TestName);
        Assert.Equal("/tmp/report with spaces.txt", attachment.Path);
    }

    [Fact]
    public void DotnetTestDetailedTranscript_ParsesErrorsAndOutput()
    {
        // Captured from `dotnet test --output Detailed` (.NET 10 SDK, xunit.v3 3.2.2 with CaptureConsole).
        string[] lines =
        [
            "Running tests from /tmp/p/bin/Debug/net10.0/MtpOut.dll (net10.0|x64)",
            "failed MtpOut.T.Fails (11ms)",
            "  System.InvalidOperationException : boom",
            "  from /tmp/p/bin/Debug/net10.0/MtpOut.dll (net10.0|x64)",
            "  Xunit.MicrosoftTestingPlatform.XunitException: System.InvalidOperationException : boom",
            "    at MtpOut.T.Fails() in /tmp/p/Tests.cs:6",
            "    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)",
            "  Standard output",
            "    before failing",
            "    ",
            "  Error output",
            "    stderr detail",
            "passed MtpOut.T.Passes (0ms)",
            "  from /tmp/p/bin/Debug/net10.0/MtpOut.dll (net10.0|x64)",
            "  Standard output",
            "    helper line",
            "    console line",
            "    ",
            "  Error output",
            "/tmp/p/bin/Debug/net10.0/MtpOut.dll (net10.0|x64) failed with 1 error(s) (267ms)",
            "Exit code: 2",
            "  Standard output: xUnit.net v3 Microsoft.Testing.Platform v1 Runner v3.2.2+728c1dce01 (64-bit .NET 10.0.13)",
            "  ",
            "",
            "Test run summary: Failed!",
            "  total: 2",
        ];

        var sut = new MtpStdoutParser();
        var results = lines.Select(sut.ProcessLine).ToList();
        results.Add(sut.Flush());
        var parsed = results.OfType<MtpParsedResult>().ToList();

        Assert.Equal(2, parsed.Count);
        var failed = parsed[0];
        Assert.Equal("MtpOut.T.Fails", failed.FullyQualifiedName);
        Assert.Contains("boom", failed.ErrorMessage);
        Assert.Contains("at MtpOut.T.Fails()", failed.StackTrace);
        Assert.Equal("before failing", failed.Output);
        Assert.Equal("stderr detail", failed.ErrorOutput);

        var passed = parsed[1];
        Assert.Equal("MtpOut.T.Passes", passed.FullyQualifiedName);
        Assert.Equal(string.Join(Environment.NewLine, "helper line", "console line"), passed.Output);
        Assert.Null(passed.ErrorOutput);
    }

    [Fact]
    public void LargeOutputAndFailureDetails_AreBoundedWithVisibleMarkers()
    {
        var limit = RetentionLimits.MaxTestTextLength;
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("failed Ns.Class.Noisy (1ms)");
        _ = sut.ProcessLine($"  {new string('e', limit)}");
        _ = sut.ProcessLine($"  {new string('f', limit)}");
        _ = sut.ProcessLine("  Standard output");
        _ = sut.ProcessLine($"    {new string('a', limit)}");
        _ = sut.ProcessLine($"    {new string('z', limit)}");

        var result = sut.Flush()!;

        Assert.InRange(result.ErrorMessage!.Length, 1, limit);
        Assert.StartsWith(new string('e', 100), result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("characters truncated", result.ErrorMessage, StringComparison.Ordinal);
        Assert.InRange(result.Output!.Length, 1, limit);
        Assert.StartsWith(new string('a', 100), result.Output, StringComparison.Ordinal);
        Assert.EndsWith(new string('z', 100), result.Output, StringComparison.Ordinal);
        Assert.Contains("characters truncated", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortOutput_DoesNotAllocateFullRetentionBuffer()
    {
        var sut = new MtpStdoutParser();
        static void Feed(MtpStdoutParser parser)
        {
            _ = parser.ProcessLine("passed Ns.Class.Method (1ms)");
            _ = parser.ProcessLine("  Standard output");
            _ = parser.ProcessLine("    x");
        }

        Feed(sut);
        _ = sut.Flush();
        const int iterations = 100;
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            Feed(sut);
            Assert.Equal("x", sut.Flush()!.Output);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.InRange(allocated, 0, iterations * 16 * 1024);
    }

    [Fact]
    public void BoundedTextBuffer_DoesNotRetainTrailingWhitespaceAsErrorText()
    {
        var buffer = new BoundedTextBuffer(16);
        buffer.AppendLine("error");
        buffer.AppendLine(new string(' ', 10_000));

        Assert.Equal("error", buffer.GetHeadValue());
    }

    // ── Target framework ───────────────────────────────────────────────────────

    [Fact]
    public void SourceLine_CapturesTargetFramework_AndQualifiesTheKey()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.Method (3ms)");
        _ = sut.ProcessLine("  from /src/Multi/bin/Debug/net8.0/Multi.dll (net8.0|x64)");
        var first = sut.ProcessLine("passed Ns.Class.Method (2ms)");
        _ = sut.ProcessLine("  from /src/Multi/bin/Debug/net10.0/Multi.dll (net10.0|x64)");
        var second = sut.Flush();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal("net8.0", first.TargetFramework);
        Assert.Equal("Ns.Class.Method [net8.0]", first.Key);
        Assert.Equal("net10.0", second.TargetFramework);
        Assert.Equal("Ns.Class.Method [net10.0]", second.Key);
    }

    [Fact]
    public void NoSourceLine_LeavesTargetFrameworkUnknown()
    {
        var sut = new MtpStdoutParser();
        _ = sut.ProcessLine("passed Ns.Class.Method (3ms)");
        var result = sut.Flush();

        Assert.NotNull(result);
        Assert.Null(result.TargetFramework);
        Assert.Equal("Ns.Class.Method", result.Key);
    }
}
