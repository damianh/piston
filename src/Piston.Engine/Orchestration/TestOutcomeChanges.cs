// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Orchestration;

/// <summary>Which tests a run fixed and which it newly broke, compared with the results before it.</summary>
internal static class TestOutcomeChanges
{
    /// <summary>
    /// Compares each test <paramref name="current"/> ran with its earlier result in <paramref name="previous"/>,
    /// matched by result key (or, for a result without a target framework, by name). Nothing is newly failing
    /// when there are no earlier results, so the first run doesn't report every failure as new.
    /// </summary>
    public static (IReadOnlyList<string> Fixed, IReadOnlyList<string> NewlyFailing) Compare(
        IReadOnlyList<TestSuite> previous, IReadOnlyList<TestSuite> current)
    {
        var before = new Dictionary<string, bool>(StringComparer.Ordinal);
        var beforeByName = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var test in previous.SelectMany(s => s.Tests))
        {
            var failed = test.Status == TestStatus.Failed;
            before[test.ResultKey] = failed || before.GetValueOrDefault(test.ResultKey);
            beforeByName[test.FullyQualifiedName] = failed || beforeByName.GetValueOrDefault(test.FullyQualifiedName);
        }

        if (before.Count == 0)
        {
            return ([], []);
        }

        var fixedTests = new SortedSet<string>(StringComparer.Ordinal);
        var newlyFailing = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var test in current.SelectMany(s => s.Tests))
        {
            bool? failedBefore = before.TryGetValue(test.ResultKey, out var f) ? f
                : test.TargetFramework is null && beforeByName.TryGetValue(test.FullyQualifiedName, out var n) ? n
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

        // A test that passed under one framework and failed under another is not fixed.
        fixedTests.ExceptWith(newlyFailing);
        return ([.. fixedTests], [.. newlyFailing]);
    }
}
