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
        var discoveredIdentities = Identities(discoveredNodes);

        Assert.Equal(discoveredIdentities, Identities(runNodes));
        Assert.Equal(discoveredIdentities, Identities(ActionNodes(captures.GetProperty("cold-rerun"))));
        Assert.Equal(discoveredIdentities, Identities(ActionNodes(captures.GetProperty("warm-run"))));
        Assert.Equal(discoveredIdentities, Identities(ActionNodes(captures.GetProperty("warm-rerun"))));

        var sameNameNodes = discoveredNodes
            .Where(node => Property(node, "location.method") == "SameName")
            .ToArray();
        Assert.Equal(2, sameNameNodes.Length);
        Assert.Equal(hasDisplayNameCollisions,
            sameNameNodes.Select(node => Property(node, "display-name")).Distinct(StringComparer.Ordinal).Count() == 1);

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
        Assert.Equal(2, sameNames.Select(test => test.ResultKey).Distinct(StringComparer.Ordinal).Count());

        var mappedRun = runNodes.Select(node => MtpTestProcessRunner.ToTestResult(node, "probe", TestStatus.NotRun)).ToArray();
        Assert.Contains(mappedRun, test => test.ErrorMessage is not null && test.StackTrace is not null);
        var assertion = Assert.Single(runNodes, node => Property(node, "location.method") == "AssertionFailure");
        Assert.Equal(framework == "mstest" ? "2" : "", assertion.GetProperty("assert.expected").GetString());
        Assert.Equal(framework == "mstest" ? "1" : "", assertion.GetProperty("assert.actual").GetString());
        Assert.Equal(runNodes.Count, runNodes.Select(node => Property(node, "uid")).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Identities_DetectUidsReassignedBetweenCases()
    {
        var discovered = Identities([Node("a", "Case(1)"), Node("b", "Case(2)")]);
        var reassigned = Identities([Node("b", "Case(1)"), Node("a", "Case(2)")]);

        Assert.NotEqual(discovered, reassigned);

        static JsonElement Node(string uid, string displayName) =>
            JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["uid"] = uid,
                ["location.type"] = "Probe.Tests",
                ["location.method"] = "Case",
                ["display-name"] = displayName,
            });
    }

    private static IReadOnlyList<JsonElement> ActionNodes(JsonElement nodes) =>
        nodes.EnumerateArray()
            .Where(node => Property(node, "node-type") == "action")
            .Select(node => node.Clone())
            .ToArray();

    private static string? Property(JsonElement node, string name) =>
        node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string Uid, string Type, string Method, string DisplayName)[] Identities(IReadOnlyList<JsonElement> nodes) =>
        nodes.Select(node => (
                Uid: RequiredProperty(node, "uid"),
                Type: RequiredProperty(node, "location.type"),
                Method: RequiredProperty(node, "location.method"),
                DisplayName: RequiredProperty(node, "display-name")))
            .OrderBy(identity => identity.Uid, StringComparer.Ordinal)
            .ToArray();

    private static string RequiredProperty(JsonElement node, string name) =>
        Property(node, name) ?? throw new InvalidDataException($"A recorded test node has no {name}.");
}
