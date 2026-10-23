// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Orchestration;

/// <summary>Which tests a run fixed and which it newly broke, compared with the results before it.</summary>
internal static class TestOutcomeChanges
{
    /// <summary>
    /// Compares each test <paramref name="current"/> ran with its earlier result in <paramref name="previous"/>,
    /// matched by suite and result key (or, for a result without a target framework, by suite and name). Nothing is newly failing
    /// when there are no earlier results, so the first run doesn't report every failure as new.
    /// </summary>
    public static (IReadOnlyList<string> Fixed, IReadOnlyList<string> NewlyFailing) Compare(
        IReadOnlyList<TestSuite> previous, IReadOnlyList<TestSuite> current)
    {
        var before = new Dictionary<(string Suite, string Key), bool>();
        var beforeByName = new Dictionary<(string Suite, string Name), bool>();
        foreach (var suite in previous)
        {
            foreach (var test in suite.Tests)
            {
                var failed = test.Status == TestStatus.Failed;
                var key = (suite.Name, test.ResultKey);
                var name = (suite.Name, test.FullyQualifiedName);
                before[key] = failed || before.GetValueOrDefault(key);
                beforeByName[name] = failed || beforeByName.GetValueOrDefault(name);
            }
        }

        if (before.Count == 0)
        {
            return ([], []);
        }

        var fixedTests = new SortedSet<string>(StringComparer.Ordinal);
        var newlyFailing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var suite in current)
        {
            foreach (var test in suite.Tests)
            {
                bool? failedBefore = before.TryGetValue((suite.Name, test.ResultKey), out var f) ? f
                    : test.TargetFramework is null && beforeByName.TryGetValue((suite.Name, test.FullyQualifiedName), out var n) ? n
                    : null;
                if (test.Status == TestStatus.Passed && failedBefore == true)
                {
                    _ = fixedTests.Add(test.ResultKey);
                }
                else if (test.Status == TestStatus.Failed && failedBefore != true)
                {
                    _ = newlyFailing.Add(test.ResultKey);
                }
            }
        }

        // A test that passed under one framework and failed under another is not fixed.
        fixedTests.ExceptWith(newlyFailing);
        return ([.. fixedTests], [.. newlyFailing]);
    }
}
