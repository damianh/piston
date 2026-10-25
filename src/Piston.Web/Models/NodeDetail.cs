// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;
using Piston.Protocol.Messages;

namespace Piston.Web.Models;

/// <summary>What the details pane shows for a selected project, framework, namespace, class or group node.</summary>
/// <param name="Structure">What the node groups.</param>
/// <param name="Title">The node's name.</param>
/// <param name="Subtitle">The node's full name, when the title is shortened.</param>
/// <param name="Tests">The node's results: a project's are all of them, other nodes' are those shown in the tree.</param>
/// <param name="Projects">The projects the results come from, in name order.</param>
/// <param name="ProjectOf">The project of each result, by reference.</param>
/// <param name="RunId">For a project, its latest completed run; a change means a newer run log is available.</param>
/// <param name="Run">The request that runs the node's tests, or null when none of them can be located.</param>
public sealed record NodeDetail(
    NodeStructure Structure,
    string Title,
    string? Subtitle,
    IReadOnlyList<TestResultDto> Tests,
    IReadOnlyList<string> Projects,
    IReadOnlyDictionary<TestResultDto, string> ProjectOf,
    string? RunId,
    RunTestsCommand? Run)
{
    public IReadOnlyList<string> DisplayProjects { get; init; } = [];

    /// <summary>
    /// Details of the node with <paramref name="key"/> (its expansion key), or null when it no longer exists.
    /// A project resolves in any grouping; other nodes only while they are in <paramref name="tree"/>.
    /// </summary>
    /// <param name="displayed">The results shown, including any live overlay.</param>
    /// <param name="completed">The results of completed runs, which carry each project's latest run id.</param>
    public static NodeDetail? For(
        string key,
        IReadOnlyList<TestSuiteDto> displayed,
        IReadOnlyList<TestSuiteDto> completed,
        IEnumerable<TestNode> tree)
    {
        var projectOf = new Dictionary<TestResultDto, string>(ReferenceEqualityComparer.Instance);
        foreach (var suite in displayed)
        {
            foreach (var test in suite.Tests)
            {
                _ = projectOf.TryAdd(test, suite.ProjectId ?? suite.Name);
            }
        }

        var structure = TestNode.StructureOf(key);
        if (structure == NodeStructure.Project)
        {
            var project = key["suite:".Length..];
            return TestTreeBuilder.ForProject(displayed, project) is { } suite
                ? new(structure, TestTreeBuilder.DisplayProjectName(displayed, project), null, suite.Tests, [project], projectOf,
                    TestTreeBuilder.ForProject(completed, project)?.RunId,
                    new RunTestsCommand([project]))
                {
                    DisplayProjects = [TestTreeBuilder.DisplayProjectName(displayed, project)],
                }
                : null;
        }

        if (structure == NodeStructure.None || TestNode.Find(tree, key) is not { } node)
        {
            return null;
        }

        var tests = node.Tests().ToList();
        var projects = tests
            .Select(t => projectOf.GetValueOrDefault(t))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        var run = projects.Count == 0
            ? null
            : new RunTestsCommand(
                projects,
                [.. tests.Select(t => t.FullyQualifiedName).Distinct(StringComparer.Ordinal)]);

        var (title, subtitle) = structure switch
        {
            NodeStructure.Status => (node.GroupKey is { } g && g.IndexOf(' ') is var i and > 0 ? g[(i + 1)..] : node.Label, null),
            NodeStructure.Pinned => ("Pinned", null),
            NodeStructure.Namespace => (node.Label, node.GroupKey),
            NodeStructure.Class => (node.Label, node.GroupKey),
            _ => (node.Label, (string?)null),
        };

        return new(structure, title, subtitle == title ? null : subtitle, tests, projects, projectOf, null, run)
        {
            DisplayProjects = [.. projects.Select(project => TestTreeBuilder.DisplayProjectName(displayed, project))],
        };
    }
}
