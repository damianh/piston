// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TrxResultParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", name);

    [Fact]
    public void Parse_LargeTextFields_AreTruncatedAtTheRetentionLimit()
    {
        var limit = RetentionLimits.MaxTestTextLength;
        var largeText = new string('x', limit * 2);
        var trx = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="t1" testName="Tests.Noisy" outcome="Failed" duration="00:00:00.001">
                  <Output>
                    <StdOut>{largeText}</StdOut>
                    <ErrorInfo><Message>{largeText}</Message><StackTrace>{largeText}</StackTrace></ErrorInfo>
                  </Output>
                </UnitTestResult>
              </Results>
              <TestDefinitions><UnitTest id="t1"><TestMethod className="Tests" name="Noisy" /></UnitTest></TestDefinitions>
            </TestRun>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"piston-trx-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, trx);
        try
        {
            var result = Assert.Single(new TrxResultParser().Parse(path)[0].Tests);
            Assert.InRange(result.Output!.Length, 1, limit);
            Assert.StartsWith(new string('x', 100), result.Output, StringComparison.Ordinal);
            Assert.EndsWith(new string('x', 100), result.Output, StringComparison.Ordinal);
            Assert.Contains("characters truncated", result.Output, StringComparison.Ordinal);
            Assert.InRange(result.ErrorMessage!.Length, 1, limit);
            Assert.Contains("characters truncated", result.ErrorMessage, StringComparison.Ordinal);
            Assert.InRange(result.StackTrace!.Length, 1, limit);
            Assert.Contains("characters truncated", result.StackTrace, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_MixedResults_ReturnsSingleSuite()
    {
        var sut = new TrxResultParser();

        var suites = sut.Parse(FixturePath("sample-mixed.trx"));

        _ = Assert.Single(suites);
    }

    [Fact]
    public void Parse_MixedResults_CorrectTestCounts()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-mixed.trx"))[0];

        Assert.Equal(3, suite.Tests.Count);
        _ = Assert.Single(suite.Tests, t => t.Status == TestStatus.Passed);
        _ = Assert.Single(suite.Tests, t => t.Status == TestStatus.Failed);
        _ = Assert.Single(suite.Tests, t => t.Status == TestStatus.Skipped);
    }

    [Fact]
    public void Parse_MixedResults_PassedTest_HasCorrectFields()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-mixed.trx"))[0];
        var passed = suite.Tests.Single(t => t.Status == TestStatus.Passed);

        Assert.Equal("AddNumbers_ReturnsCorrectSum", passed.DisplayName);
        Assert.Equal("MyProject.Tests.MathTests.AddNumbers_ReturnsCorrectSum", passed.FullyQualifiedName);
        Assert.Equal(TimeSpan.FromMilliseconds(23), passed.Duration);
        Assert.Equal("Adding 2 + 3", passed.Output);
        Assert.Null(passed.ErrorMessage);
        Assert.Null(passed.StackTrace);
    }

    [Fact]
    public void Parse_MixedResults_FailedTest_HasErrorInfo()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-mixed.trx"))[0];
        var failed = suite.Tests.Single(t => t.Status == TestStatus.Failed);

        Assert.Equal("Subtract_ThrowsOnNegative", failed.DisplayName);
        Assert.NotNull(failed.ErrorMessage);
        Assert.Contains("Assert.Equal() Failure", failed.ErrorMessage);
        Assert.NotNull(failed.StackTrace);
        Assert.Contains("MathTests.cs:line 42", failed.StackTrace);
    }

    [Fact]
    public void Parse_MixedResults_SuiteDuration_IsComputedFromTimes()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-mixed.trx"))[0];

        // start=10:00:00.200, finish=10:00:01.000 => 800ms
        Assert.Equal(TimeSpan.FromMilliseconds(800), suite.TotalDuration);
    }

    [Fact]
    public void Parse_AllPassed_CorrectCounts()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-all-passed.trx"))[0];

        Assert.Equal(2, suite.Tests.Count);
        Assert.All(suite.Tests, t => Assert.Equal(TestStatus.Passed, t.Status));
    }

    [Fact]
    public void Parse_AllPassed_FullyQualifiedNames_IncludeClassName()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-all-passed.trx"))[0];

        Assert.All(suite.Tests, t =>
            Assert.StartsWith("MyProject.Tests.SuiteA.", t.FullyQualifiedName));
    }

    [Fact]
    public void Parse_QualifiedTestName_DoesNotDoubleClassName()
    {
        // xUnit writes testName as "Namespace.Class.Method" and className as "Namespace.Class".
        const string trx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="t1" testName="Shop.Tests.PriceTests.Boundary(amount: 1)" outcome="Passed" duration="00:00:00.001" />
              </Results>
              <TestDefinitions>
                <UnitTest id="t1"><TestMethod className="Shop.Tests.PriceTests" name="Boundary" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"piston-trx-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, trx);
        try
        {
            var test = Assert.Single(new TrxResultParser().Parse(path)[0].Tests);
            Assert.Equal("Shop.Tests.PriceTests.Boundary(amount: 1)", test.FullyQualifiedName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_KeepsTheMethodOfATestWithACustomDisplayName()
    {
        const string trx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="t1" testName="nice name" outcome="Passed" duration="00:00:00.001" />
              </Results>
              <TestDefinitions>
                <UnitTest id="t1"><TestMethod className="Shop.Tests.PriceTests" name="Boundary" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"piston-trx-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, trx);
        try
        {
            var test = Assert.Single(new TrxResultParser().Parse(path)[0].Tests);
            Assert.Equal("Shop.Tests.PriceTests.nice name", test.FullyQualifiedName);
            Assert.Equal("Shop.Tests.PriceTests.Boundary", test.Method);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_SeparatesStdOutStdErrAndDebugTrace()
    {
        const string trx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="t1" testName="Shop.Tests.PriceTests.Logs" outcome="Passed" duration="00:00:00.001">
                  <Output>
                    <StdOut>to stdout</StdOut>
                    <StdErr>to stderr</StdErr>
                    <DebugTrace>to trace</DebugTrace>
                  </Output>
                </UnitTestResult>
                <UnitTestResult testId="t2" testName="Shop.Tests.PriceTests.ErrOnly" outcome="Passed" duration="00:00:00.001">
                  <Output>
                    <StdOut>   </StdOut>
                    <StdErr>only stderr</StdErr>
                  </Output>
                </UnitTestResult>
              </Results>
              <TestDefinitions>
                <UnitTest id="t1"><TestMethod className="Shop.Tests.PriceTests" name="Logs" /></UnitTest>
                <UnitTest id="t2"><TestMethod className="Shop.Tests.PriceTests" name="ErrOnly" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """;
        var path = Path.Combine(Path.GetTempPath(), $"piston-trx-{Guid.NewGuid():N}.trx");
        File.WriteAllText(path, trx);
        try
        {
            var tests = new TrxResultParser().Parse(path)[0].Tests;
            var logs = tests.Single(t => t.FullyQualifiedName.EndsWith(".Logs", StringComparison.Ordinal));
            Assert.Equal(string.Join(Environment.NewLine, "to stdout", "to trace"), logs.Output);
            Assert.Equal("to stderr", logs.ErrorOutput);

            var errorOnly = tests.Single(t => t.FullyQualifiedName.EndsWith(".ErrOnly", StringComparison.Ordinal));
            Assert.Null(errorOnly.Output);
            Assert.Equal("only stderr", errorOnly.ErrorOutput);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Parse_ResolvesResultFilesRelativeToTheTrxFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"piston-trx-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        var trxPath = Path.Combine(directory, "results.trx");
        const string trx = """
            <?xml version="1.0" encoding="UTF-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="t1" testName="Tests.Captures" outcome="Passed" duration="00:00:00.001">
                  <ResultFiles><ResultFile path="attachment.txt" /></ResultFiles>
                </UnitTestResult>
              </Results>
              <TestDefinitions><UnitTest id="t1"><TestMethod className="Tests" name="Captures" /></UnitTest></TestDefinitions>
            </TestRun>
            """;
        File.WriteAllText(trxPath, trx);
        File.WriteAllText(Path.Combine(directory, "attachment.txt"), "artifact");
        try
        {
            var attachment = Assert.Single(new TrxResultParser().Parse(trxPath)[0].Tests[0].Attachments);

            Assert.Equal(Path.Combine(directory, "attachment.txt"), attachment.Path);
            Assert.Equal("attachment.txt", attachment.DisplayName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Parse_ResolvesResultFilesUsingTheLoggerDeploymentLayout()
    {
        var directory = Directory.CreateTempSubdirectory("piston-trx-").FullName;
        try
        {
            const string executionId = "01234567-89ab-cdef-0123-456789abcdef";
            var deploymentDirectory = Path.Combine(directory, "run", "In", executionId, "machine");
            _ = Directory.CreateDirectory(deploymentDirectory);
            var source = Path.Combine(deploymentDirectory, "report.txt");
            File.WriteAllText(source, "test artifact");
            var trxPath = Path.Combine(directory, "results.trx");
            File.WriteAllText(trxPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                  <TestSettings><Deployment runDeploymentRoot="run" /></TestSettings>
                  <Results>
                    <UnitTestResult testId="t1" testName="Tests.Captures" outcome="Passed"
                                    relativeResultsDirectory="{executionId}">
                      <ResultFiles><ResultFile path="machine/report.txt" /></ResultFiles>
                    </UnitTestResult>
                  </Results>
                </TestRun>
                """);

            var suites = new TrxResultParser().Parse(trxPath);
            var attachment = Assert.Single(suites[0].Tests[0].Attachments);

            Assert.Equal(source, attachment.Path);
            var store = new TestArtifactStore(directory);
            var retained = store.Retain("0123456789abcdef0123456789abcdef", suites);
            var copied = Assert.Single(retained[0].Tests[0].Attachments);
            Assert.True(copied.Retained);
            Directory.Delete(Path.Combine(directory, "run"), recursive: true);
            Assert.Equal("test artifact", File.ReadAllText(store.Resolve(copied)!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
