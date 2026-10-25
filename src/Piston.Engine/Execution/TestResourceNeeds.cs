// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>The shared resources a test project's run holds, as inferred and configured.</summary>
/// <param name="ProjectPath">The test project.</param>
/// <param name="Keys">Exclusive resource keys (see <see cref="TestResourceKey"/>), sorted and distinct.</param>
/// <param name="UsesContainers">Whether the run counts against the machine's <c>containerSlots</c>.</param>
/// <param name="Reasons">Why: the package references and <c>testResources</c> rules that applied.</param>
public sealed record TestResourceNeeds(
    string ProjectPath,
    IReadOnlyList<string> Keys,
    bool UsesContainers,
    IReadOnlyList<string> Reasons)
{
    /// <summary>True when the run needs no coordination with other test runs.</summary>
    public bool IsEmpty => Keys.Count == 0 && !UsesContainers;

    public static TestResourceNeeds None(string projectPath) => new(projectPath, [], false, []);

    /// <summary>
    /// The needs of one process that runs several projects (a whole-solution run): every key, containers
    /// when any project uses them.
    /// </summary>
    public static TestResourceNeeds Combine(string target, IEnumerable<TestResourceNeeds> needs)
    {
        var list = needs.Where(n => !n.IsEmpty).ToList();
        return new TestResourceNeeds(
            target,
            [.. list.SelectMany(n => n.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
            list.Any(n => n.UsesContainers),
            [.. list.SelectMany(n => n.Reasons.Select(r => $"{Path.GetFileNameWithoutExtension(n.ProjectPath)}: {r}"))]);
    }
}
