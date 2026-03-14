using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Models;

public sealed class ProjectTestModelTests
{
    [Fact]
    public void ProjectTestRequest_ConstructsCorrectly()
    {
        var request = new ProjectTestRequest("path/to/Project.csproj", "MyFilter", true);

        Assert.Equal("path/to/Project.csproj", request.ProjectPath);
        Assert.Equal("MyFilter", request.Filter);
        Assert.True(request.CollectCoverage);
    }

    [Fact]
    public void ProjectTestRequest_NullFilter_IsAllowed()
    {
        var request = new ProjectTestRequest("path/to/Project.csproj", null, false);

        Assert.Null(request.Filter);
        Assert.False(request.CollectCoverage);
    }

    [Fact]
    public void ProjectTestResult_NotCrashed_HasCorrectDefaults()
    {
        var result = new ProjectTestResult("path/to/Project.csproj", [], null, [], Crashed: false);

        Assert.Equal("path/to/Project.csproj", result.ProjectPath);
        Assert.Empty(result.Suites);
        Assert.Null(result.RunnerError);
        Assert.Empty(result.CoverageReportPaths);
        Assert.False(result.Crashed);
    }

    [Fact]
    public void ProjectTestResult_Crashed_FlagIsTrue()
    {
        var result = new ProjectTestResult("path/to/Project.csproj", [], "Something exploded", [], Crashed: true);

        Assert.True(result.Crashed);
        Assert.Equal("Something exploded", result.RunnerError);
    }

    [Fact]
    public void ProjectTestResult_RecordEquality_WorksCorrectly()
    {
        var a = new ProjectTestResult("proj.csproj", [], null, [], Crashed: false);
        var b = new ProjectTestResult("proj.csproj", [], null, [], Crashed: false);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ProjectTestResult_WithSuites_ContainsSuites()
    {
        var suites = new List<TestSuite>
        {
            new("MySuite", [], DateTimeOffset.UtcNow, TimeSpan.Zero)
        };

        var result = new ProjectTestResult("proj.csproj", suites, null, [], Crashed: false);

        Assert.Single(result.Suites);
        Assert.Equal("MySuite", result.Suites[0].Name);
    }
}
