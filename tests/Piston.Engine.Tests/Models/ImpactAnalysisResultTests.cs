// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Xunit;

namespace Piston.Engine.Tests.Models;

public sealed class ImpactAnalysisResultTests
{
    private static ImpactAnalysisResult WithTests(int count) =>
        new([], ["/t/T.csproj"], RequiresGraphRebuild: false, IsFullRun: false)
        {
            AffectedTestFqns = count < 0 ? null : [.. Enumerable.Range(0, count).Select(i => $"T.Test{i}")],
            Explanation = "Selected by changed lines.",
        };

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(ImpactAnalysisResult.MaxFilteredTests, true)]
    [InlineData(ImpactAnalysisResult.MaxFilteredTests + 1, false)]
    public void NarrowsToTests_OnlyForASelectionARunCanNameTestByTest(int count, bool narrows) =>
        Assert.Equal(narrows, WithTests(count).NarrowsToTests);

    [Fact]
    public void RunExplanation_WithinTheLimit_IsTheExplanation() =>
        Assert.Equal("Selected by changed lines.", WithTests(3).RunExplanation);

    [Fact]
    public void RunExplanation_OverTheLimit_SaysTheProjectsRunWhole()
    {
        var explanation = WithTests(ImpactAnalysisResult.MaxFilteredTests + 1).RunExplanation;

        Assert.Equal(
            $"{ImpactAnalysisResult.MaxFilteredTests + 1} tests were selected, more than the {ImpactAnalysisResult.MaxFilteredTests} a run narrows to, so tests are selected by project.",
            explanation);
    }
}
