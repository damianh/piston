// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Engine.Models;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class MtpRecordedNodeTests
{
    [Theory]
    [InlineData("mstest", true)]
    [InlineData("nunit", true)]
    [InlineData("tunit", true)]
    [InlineData("xunit-v3", false)]
    public void RecordedNodes_ShowFrameworkIdentityAndStructuredFields(string framework, bool hasDisplayNameCollisions)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Services", "MtpNodes", $"{framework}.json")));
        var captures = document.RootElement.GetProperty("captures");
        var discoveredNodes = ActionNodes(captures.GetProperty("discover"));
        var runNodes = ActionNodes(captures.GetProperty("run"));
        var discoveredUids = Uids(discoveredNodes);

        Assert.Equal(discoveredUids, Uids(runNodes));
        Assert.Equal(discoveredUids, Uids(ActionNodes(captures.GetProperty("cold-rerun"))));
        Assert.Equal(discoveredUids, Uids(ActionNodes(captures.GetProperty("warm-run"))));
        Assert.Equal(discoveredUids, Uids(ActionNodes(captures.GetProperty("warm-rerun"))));

        var discovered = new List<TestResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Assert.True(MtpTestProcessRunner.AddDiscovered(
            discovered,
            seen,
            discoveredNodes,
            filter: null,
            appPath: "probe"));

        var sameNames = discovered.Where(test => test.DisplayName == "SameName").ToArray();
        Assert.Equal(2, sameNames.Length);
        Assert.Equal(2, sameNames.Select(test => test.Method).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(hasDisplayNameCollisions, sameNames.Select(test => test.ResultKey).Distinct(StringComparer.Ordinal).Count() == 1);

        var mappedRun = runNodes.Select(node => MtpTestProcessRunner.ToTestResult(node, "probe", TestStatus.NotRun)).ToArray();
        Assert.Contains(mappedRun, test => test.ErrorMessage is not null && test.StackTrace is not null);
        Assert.Contains(runNodes, node => node.TryGetProperty("assert.expected", out _) && node.TryGetProperty("assert.actual", out _));
        Assert.Equal(runNodes.Count, runNodes.Select(node => Property(node, "uid")).Distinct(StringComparer.Ordinal).Count());
    }

    private static IReadOnlyList<JsonElement> ActionNodes(JsonElement nodes) =>
        nodes.EnumerateArray()
            .Where(node => Property(node, "node-type") == "action")
            .Select(node => node.Clone())
            .ToArray();

    private static string? Property(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string[] Uids(IReadOnlyList<JsonElement> nodes) =>
        nodes.Select(node => Property(node, "uid") ?? throw new InvalidDataException("A recorded test node has no UID."))
            .OrderBy(uid => uid, StringComparer.Ordinal)
            .ToArray();
}
