// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Web.Models;
using Xunit;

namespace Piston.Tests.Web;

public sealed class ViewStateTests
{
    private static TestResultDto Result(string? framework = null) =>
        new("Shared.Test", "Shared.Test", TestStatusDto.Passed, 1, null, null, null, null)
        {
            TargetFramework = framework,
        };

    [Theory]
    [InlineData(null)]
    [InlineData("net10.0")]
    public void RevealingATestKeepsItsProjectAcrossDuplicateNames(string? framework)
    {
        var first = Result(framework);
        var second = Result(framework);
        TestSuiteDto[] suites =
        [
            new("A.Tests", [first], DateTimeOffset.UnixEpoch, 1),
            new("B.Tests", [second], DateTimeOffset.UnixEpoch, 1),
        ];
        var view = new ViewState { SelectedTestKey = first.Key, SelectedTestProject = "A.Tests", SelectedNodeKey = "old" };

        view.RevealTest(new TestReference("B.Tests", second.Key));

        Assert.Null(view.SelectedNodeKey);
        Assert.True(view.RevealSelectedTest);
        Assert.Equal("B.Tests", view.SelectedTestProject);
        Assert.Same(second, view.FindSelectedTest(suites));
        Assert.Null(view.FindSelectedTest([suites[0]]));
    }

    [Theory]
    [InlineData("Shared.Test")]
    [InlineData("Shared.Test [net9.0]")]
    public void FrameworkFallbackStaysWithinTheSelectedProject(string key)
    {
        var first = Result("net10.0");
        var second = Result("net10.0");
        var view = new ViewState();
        view.RevealTest(new TestReference("B.Tests", key));

        Assert.Same(second, view.FindSelectedTest(
        [
            new("A.Tests", [first], DateTimeOffset.UnixEpoch, 1),
            new("B.Tests", [second], DateTimeOffset.UnixEpoch, 1),
        ]));
    }

    [Theory]
    [InlineData("/work/B.Tests/B.Tests.csproj")]
    [InlineData(@"C:\work\B.Tests\B.Tests.csproj")]
    public void CoverageReferencesUseTheSuiteNameForEitherHostPathFormat(string projectPath) =>
        Assert.Equal(new TestReference("B.Tests", "Shared.Test"),
            TestReference.FromProjectPath(projectPath, "Shared.Test"));
}
