// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>
/// Resolves the needs of a run target: a test project, or a solution whose single test process runs every
/// test project and so needs all their resources.
/// </summary>
internal sealed class TestResourceResolver(
    TestResourceInference inference, Func<IReadOnlyList<string>> allTestProjects, Action<string>? log = null)
{
    private int _checkedRules;

    public TestResourceNeeds Resolve(string target)
    {
        var projects = allTestProjects();
        if (projects.Count > 0 && Interlocked.Exchange(ref _checkedRules, 1) == 0)
        {
            foreach (var pattern in inference.UnmatchedRules(projects))
            {
                log?.Invoke($"testResources entry '{pattern}' matches no test project in the solution.");
            }
        }

        return IsSolution(target)
            ? TestResourceNeeds.Combine(target, projects.Select(inference.Resolve))
            : inference.Resolve(target);
    }

    private static bool IsSolution(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx" or ".slnf";
}
