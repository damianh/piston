// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Remembers when each test was first reported <see cref="TestStatus.Running"/>, from live progress
/// snapshots, so the longest-running test can be found. Not thread-safe.
/// </summary>
internal sealed class RunningTestClock(TimeProvider time)
{
    private readonly Dictionary<(string Project, string Test), DateTimeOffset> _started = [];

    /// <summary>Replaces <paramref name="projectPath"/>'s running tests with those in <paramref name="suites"/>.</summary>
    public void Observe(string projectPath, IReadOnlyList<TestSuite> suites)
    {
        var running = suites
            .SelectMany(s => s.Tests)
            .Where(t => t.Status == TestStatus.Running)
            .Select(t => t.ResultKey)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var key in _started.Keys.Where(k => k.Project == projectPath && !running.Contains(k.Test)).ToList())
        {
            _ = _started.Remove(key);
        }

        var now = time.GetUtcNow();
        foreach (var test in running)
        {
            _ = _started.TryAdd((projectPath, test), now);
        }
    }

    /// <summary>Forgets <paramref name="projectPath"/>'s running tests.</summary>
    public void Clear(string projectPath) => Observe(projectPath, []);

    /// <summary>The running tests, longest-running first.</summary>
    public IReadOnlyList<(string ProjectPath, string Test, DateTimeOffset StartedAt, TimeSpan Elapsed)> Running()
    {
        var now = time.GetUtcNow();
        return _started
            .OrderBy(kv => kv.Value)
            .ThenBy(kv => kv.Key.Test, StringComparer.Ordinal)
            .Select(kv => (kv.Key.Project, kv.Key.Test, kv.Value, now - kv.Value))
            .ToList();
    }

    /// <summary>The test that has been running longest, or null when none is running.</summary>
    public (string ProjectPath, string Test, DateTimeOffset StartedAt, TimeSpan Elapsed)? Longest() =>
        Running() is [var longest, ..] ? longest : null;
}
