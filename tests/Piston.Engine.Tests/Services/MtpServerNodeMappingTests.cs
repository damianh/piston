// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpServerNodeMappingTests
{
    // Shape of a node reported by xunit.v3 in Microsoft.Testing.Platform server mode.
    private static JsonElement Node(string extra = "") => JsonDocument.Parse($$"""
        {
          "uid": "abc",
          "display-name": "Mt.Deep.A.Th(n: 1)",
          "location.type": "Mt.Deep.A",
          "location.method": "Th(System.Int32)",
          "node-type": "action",
          "execution-state": "discovered",
          "traits": [ { "key": "Category", "value": "Slow" }, { "Owner": "damian" } ]
          {{extra}}
        }
        """).RootElement;

    [Theory]
    [InlineData("FullyQualifiedName", new[] { "Mt.Deep.A.Th(n: 1)", "Mt.Deep.A.Th" })]
    [InlineData("Name", new[] { "Mt.Deep.A.Th(n: 1)", "Th" })]
    [InlineData("ClassName", new[] { "Mt.Deep.A" })]
    [InlineData("Category", new[] { "Slow" })]
    [InlineData("TestCategory", new[] { "Slow" })]
    [InlineData("owner", new[] { "damian" })]
    [InlineData("Priority", new string[0])]
    public void FilterValues_MapVsTestPropertiesToNodeFields(string property, string[] expected) =>
        Assert.Equal(expected, MtpTestProcessRunner.FilterValues(Node(), property));

    [Fact]
    public void FilterExpression_MatchesNodesLikeDotnetTest()
    {
        bool Matches(string filter)
        {
            Assert.True(TestFilterExpression.TryParse(filter, out var expression));
            return expression!.Matches(p => MtpTestProcessRunner.FilterValues(Node(), p));
        }

        Assert.True(Matches("FullyQualifiedName=Mt.Deep.A.Th"));
        Assert.True(Matches("Mt.Deep.A"));
        Assert.True(Matches("Category=Slow&ClassName=Mt.Deep.A"));
        Assert.False(Matches("Category!=Slow"));
    }

    [Theory]
    [InlineData("in-progress", TestStatus.Running)]
    [InlineData("passed", TestStatus.Passed)]
    [InlineData("skipped", TestStatus.Skipped)]
    [InlineData("failed", TestStatus.Failed)]
    [InlineData("timed-out", TestStatus.Failed)]
    [InlineData("error", TestStatus.Failed)]
    [InlineData("discovered", TestStatus.NotRun)]
    [InlineData(null, TestStatus.NotRun)]
    public void MapState_MapsExecutionStates(string? state, TestStatus expected) =>
        Assert.Equal(expected, MtpTestProcessRunner.MapState(state));

    [Fact]
    public void ToTestResult_CarriesDurationErrorAndOutput()
    {
        var node = Node("""
            , "time.duration-ms": 12.5,
            "error.message": "Assert.Equal() Failure",
            "assert.expected": "1",
            "assert.actual": "2",
            "error.stacktrace": "at Mt.Deep.A.Th()",
            "standardOutput": "hello"
            """);

        var result = MtpTestProcessRunner.ToTestResult(node, "/bin/Mt.dll", TestStatus.Failed);

        Assert.Equal("Mt.Deep.A.Th(n: 1)", result.FullyQualifiedName);
        Assert.Equal(TestStatus.Failed, result.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(12.5), result.Duration);
        Assert.StartsWith("Assert.Equal() Failure", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Expected: 1", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Actual:   2", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal("at Mt.Deep.A.Th()", result.StackTrace);
        Assert.Equal("hello", result.Output);
        Assert.Equal("/bin/Mt.dll", result.Source);
    }

    private static JsonElement Named(string uid, string state) => JsonDocument.Parse($$"""
        { "uid": "{{uid}}", "display-name": "Mt.A.Same", "node-type": "action", "execution-state": "{{state}}" }
        """).RootElement;

    [Fact]
    public void AddDiscovered_KeepsDistinctNodesSharingADisplayName_AndDropsRepeatedUpdates()
    {
        var tests = new List<TestResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Assert.True(MtpTestProcessRunner.AddDiscovered(tests, seen, [Named("1", "discovered"), Named("2", "discovered")], null, "/bin/Mt.dll"));
        Assert.False(MtpTestProcessRunner.AddDiscovered(tests, seen, [Named("1", "discovered")], null, "/bin/Mt.dll"));

        Assert.Equal(2, tests.Count);
        Assert.All(tests, t => Assert.Equal("Mt.A.Same", t.FullyQualifiedName));
    }

    [Fact]
    public void ApplyRunUpdates_KeepsOppositeOutcomesOfNodesSharingADisplayName()
    {
        var live = new Dictionary<string, TestResult>(StringComparer.Ordinal);

        MtpTestProcessRunner.ApplyRunUpdates(live, [Named("1", "in-progress"), Named("2", "in-progress")], "/bin/Mt.dll");
        MtpTestProcessRunner.ApplyRunUpdates(live, [Named("1", "failed")], "/bin/Mt.dll");
        MtpTestProcessRunner.ApplyRunUpdates(live, [Named("2", "passed")], "/bin/Mt.dll");

        Assert.Equal(
            [TestStatus.Failed, TestStatus.Passed],
            live.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value.Status));
        Assert.All(live.Values, t => Assert.Equal("Mt.A.Same", t.FullyQualifiedName));
    }
}
