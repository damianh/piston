using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TrxResultParserTests
{
    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Services", "Fixtures", name);

    [Fact]
    public void Parse_MixedResults_ReturnsSingleSuite()
    {
        var sut = new TrxResultParser();

        var suites = sut.Parse(FixturePath("sample-mixed.trx"));

        Assert.Single(suites);
    }

    [Fact]
    public void Parse_MixedResults_CorrectTestCounts()
    {
        var sut = new TrxResultParser();

        var suite = sut.Parse(FixturePath("sample-mixed.trx"))[0];

        Assert.Equal(3, suite.Tests.Count);
        Assert.Single(suite.Tests, t => t.Status == TestStatus.Passed);
        Assert.Single(suite.Tests, t => t.Status == TestStatus.Failed);
        Assert.Single(suite.Tests, t => t.Status == TestStatus.Skipped);
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
}
