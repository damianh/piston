// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;
using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Coverage;

/// <summary>
/// Plans the idle-time measurement of finer impact units: one coverage run per test class (or test), so a
/// change selects the classes that executed it rather than every test in the project.
/// </summary>
internal static class ImpactBackfill
{
    /// <summary>A unit to measure.</summary>
    /// <param name="ProjectPath">The test project.</param>
    /// <param name="Key">The class (or test method) name, which is the unit key.</param>
    /// <param name="Filter">The <c>dotnet test</c> filter that runs just the unit.</param>
    /// <param name="Tests">The unit's tests as last run.</param>
    internal sealed record Unit(string ProjectPath, string Key, string Filter, IReadOnlyList<string> Tests)
    {
        internal IReadOnlyList<TestResult> ExpectedResults { get; init; } = [];
    }

    /// <summary>What to measure and what to forget.</summary>
    /// <param name="Units">Units never measured first, then those whose evidence is outdated.</param>
    /// <param name="Obsolete">Recorded unit keys per project that no longer name a unit.</param>
    internal sealed record Plan(IReadOnlyList<Unit> Units, IReadOnlyDictionary<string, IReadOnlyList<string>> Obsolete);

    /// <summary>
    /// Plans the units of <paramref name="testProjects"/> at <paramref name="granularity"/> that are missing,
    /// flagged for re-measurement or whose tests have changed since they were recorded.
    /// </summary>
    /// <param name="testProjects">The test projects, matched to suites by name.</param>
    /// <param name="suites">The latest test results.</param>
    /// <param name="recorded">The units recorded for a project.</param>
    /// <param name="granularity"><see cref="ImpactGranularity.Class"/> or <see cref="ImpactGranularity.Test"/>.</param>
    /// <param name="skip">Units not to plan, e.g. ones whose measurement failed; keyed by project and unit.</param>
    public static Plan Create(
        IReadOnlyList<string> testProjects,
        IReadOnlyList<TestSuite> suites,
        Func<string, IReadOnlyList<ImpactUnitInfo>> recorded,
        ImpactGranularity granularity,
        IReadOnlySet<(string Project, string Key)>? skip = null,
        string? solutionDirectory = null)
    {
        if (granularity is not (ImpactGranularity.Class or ImpactGranularity.Test))
        {
            throw new ArgumentOutOfRangeException(nameof(granularity), granularity, "Only class and test units are measured separately.");
        }

        var missing = new List<Unit>();
        var outdated = new List<Unit>();
        var legacy = new List<Unit>();
        var obsolete = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var project in testProjects.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var suiteName = TestProjectNames.SuiteName(project);
            var projectId = solutionDirectory is null ? suiteName : TestProjectNames.ProjectId(solutionDirectory, project);
            var tests = suites
                .Where(s => string.Equals(s.ProjectId ?? s.Name, projectId, StringComparison.Ordinal))
                .SelectMany(s => s.Tests)
                .ToList();
            if (tests.Count == 0)
            {
                // Not run yet (or its results were not reported): nothing to measure, and nothing known obsolete.
                continue;
            }

            var units = GroupUnits(tests, granularity);
            var existing = recorded(project).ToDictionary(u => u.UnitKey, StringComparer.Ordinal);

            foreach (var (key, expectedResults) in units.OrderBy(u => u.Key, StringComparer.Ordinal))
            {
                if (skip?.Contains((project, key)) == true || key.Contains('"', StringComparison.Ordinal))
                {
                    continue;
                }

                var unitTests = expectedResults.Select(test => test.FullyQualifiedName)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
                var unit = new Unit(project, key, Filter(key, granularity), unitTests)
                {
                    ExpectedResults = expectedResults,
                };
                if (!existing.TryGetValue(key, out var info))
                {
                    missing.Add(unit);
                }
                else if (!info.IsCurrent || !MatchesResults(info, expectedResults, complete: true))
                {
                    outdated.Add(unit);
                }
                else if (!info.HasBranches)
                {
                    legacy.Add(unit);
                }
            }

            var gone = existing.Keys.Where(k => !units.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
            if (gone.Count > 0)
            {
                obsolete[project] = gone;
            }
        }

        return new Plan(
            [.. InterleaveProjects(missing), .. InterleaveProjects(outdated), .. InterleaveProjects(legacy)],
            obsolete);
    }

    /// <summary>
    /// Removes finer evidence that no longer represents the observed framework-qualified tests before it
    /// can override the latest run-level evidence. A filtered run only proves the results it contains.
    /// </summary>
    internal static async Task InvalidateOutdatedAsync(
        IImpactMapStore store, string project, IReadOnlyList<TestResult> results, bool complete, CancellationToken ct)
    {
        foreach (var granularity in new[] { ImpactGranularity.Class, ImpactGranularity.Test })
        {
            ct.ThrowIfCancellationRequested();
            var units = GroupUnits(results, granularity);
            var outdated = store.GetUnits(project, granularity)
                .Where(info => info.TestResultKeys is null
                    || (units.TryGetValue(info.UnitKey, out var observed)
                        ? !MatchesResults(info, observed, complete)
                        : complete))
                .Select(info => info.UnitKey)
                .ToList();
            await store.RemoveUnitsAsync(project, granularity, outdated).ConfigureAwait(false);
        }
    }

    private static Dictionary<string, IReadOnlyList<TestResult>> GroupUnits(
        IEnumerable<TestResult> tests, ImpactGranularity granularity) =>
        tests.Select(test => (Key: UnitKey(test, granularity), Test: test))
            .Where(item => item.Key is not null)
            .GroupBy(item => item.Key!, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<TestResult>)[.. group.Select(item => item.Test)],
                StringComparer.Ordinal);

    private static bool MatchesResults(ImpactUnitInfo info, IReadOnlyList<TestResult> observed, bool complete)
    {
        if (info.TestResultKeys is null)
        {
            return false;
        }

        var tests = observed.Select(test => test.FullyQualifiedName)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var resultKeys = observed.Select(test => test.ResultKey)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        return complete
            ? info.Tests.SequenceEqual(tests, StringComparer.Ordinal)
                && info.TestResultKeys.SequenceEqual(resultKeys, StringComparer.Ordinal)
            : tests.All(info.Tests.Contains) && resultKeys.All(info.TestResultKeys.Contains);
    }

    private static IEnumerable<Unit> InterleaveProjects(IEnumerable<Unit> units)
    {
        var projects = units.GroupBy(unit => unit.ProjectPath, StringComparer.Ordinal)
            .Select(group => new Queue<Unit>(group))
            .ToList();
        while (projects.Count > 0)
        {
            foreach (var project in projects)
            {
                if (project.TryDequeue(out var unit))
                {
                    yield return unit;
                }
            }

            _ = projects.RemoveAll(project => project.Count == 0);
        }
    }

    /// <summary>The class (<c>Ns.Class</c>, <c>Ns.Outer+Inner</c>) or method (<c>Ns.Class.Method</c>) a test belongs to.</summary>
    internal static string? UnitKey(TestResult test, ImpactGranularity granularity)
    {
        var method = TestSelectionFilter.MethodName(test.Method ?? test.FullyQualifiedName);
        if (granularity == ImpactGranularity.Test)
        {
            return method.Length == 0 ? null : method;
        }

        var dot = method.LastIndexOf('.');
        return dot > 0 ? method[..dot] : null;
    }

    /// <summary>A filter running exactly the unit's tests.</summary>
    internal static string Filter(string key, ImpactGranularity granularity) =>
        granularity == ImpactGranularity.Test
            ? $"FullyQualifiedName={TestSelectionFilter.Escape(key)}"
            : $"FullyQualifiedName~{TestSelectionFilter.Escape(key + ".")}";
}
