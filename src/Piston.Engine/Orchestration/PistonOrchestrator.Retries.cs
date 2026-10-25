// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Orchestration;

// Opt-in retries of a run's failed tests: re-runs only the failed tests, against the same build and without
// coverage, so a test that fails and then passes is evidence of flakiness. Retries run before the run's
// post-run source fingerprint, so every attempt belongs to the run.
public sealed partial class PistonOrchestrator
{
    /// <summary>A run with more failed tests than this is not retried: so many failures are likely a real breakage.</summary>
    public const int MaxRetriedFailures = 20;

    /// <summary>How many times a run re-runs its failed tests; 0 turns retries off.</summary>
    internal int RetryFailedTests { get; set; }

    /// <summary>
    /// Re-runs the failed tests up to <see cref="RetryFailedTests"/> times, stopping for each test once it passes.
    /// Returns the suites with each retried test's last attempt as its result and its earlier attempts in
    /// <see cref="TestResult.Attempts"/>, and why retries were skipped, if they were.
    /// </summary>
    /// <param name="targets">The run's test targets; null when the run targeted the solution.</param>
    /// <exception cref="OperationCanceledException">The run was cancelled while retrying.</exception>
    private async Task<(IReadOnlyList<TestSuite> Suites, string? SkippedReason)> RetryFailedTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? targets,
        IReadOnlyList<TestSuite> suites,
        IReadOnlyList<ProjectTestResult> projects,
        CancellationToken ct)
    {
        var limit = RetryFailedTests;
        if (limit <= 0)
        {
            return (suites, null);
        }

        // Each project's own results say which process to re-run; without them, re-run the run's targets.
        var groups = projects.Count > 0
            ? projects
                .Where(p => p.TimedOut is null)
                .Select(p => (Targets: string.Equals(p.ProjectPath, solutionPath, StringComparison.Ordinal) ? null : (IReadOnlyList<string>)[p.ProjectPath], p.Suites))
                .ToList()
            : [(Targets: targets, Suites: suites)];
        var timedOut = projects.Any(p => p.TimedOut is not null);

        var failing = groups
            .Select(g => (g.Targets, g.Suites, Failed: g.Suites
                .SelectMany(s => s.Tests
                    .GroupBy(t => t.ResultKey, StringComparer.Ordinal)
                    .Where(d => d.Count() == 1 && d.First().Status == TestStatus.Failed)
                    .Select(d => (Suite: s.Name, Test: d.First())))
                .ToList()))
            .Where(g => g.Failed.Count > 0)
            .ToList();
        var failedCount = failing.Sum(g => g.Failed.Count);
        if (failedCount == 0)
        {
            return (suites, timedOut && suites.SelectMany(s => s.Tests).Any(t => t.Status == TestStatus.Failed)
                ? "Tests of a project that timed out are not retried."
                : null);
        }

        if (failedCount > MaxRetriedFailures)
        {
            return (suites, $"{failedCount} tests failed; runs with more than {MaxRetriedFailures} failed tests are not retried.");
        }

        var log = DiagnosticLog.Instance;
        var current = new Dictionary<TestResult, TestResult>(ReferenceEqualityComparer.Instance);
        var earlier = new Dictionary<TestResult, List<TestAttempt>>(ReferenceEqualityComparer.Instance);
        var unretriable = new HashSet<TestResult>(ReferenceEqualityComparer.Instance);
        foreach (var (_, _, failed) in failing)
        {
            foreach (var (_, test) in failed)
            {
                current[test] = test;
            }
        }

        try
        {
            for (var attempt = 1; attempt <= limit; attempt++)
            {
                var pending = failing
                    .Select(g => (g.Targets, g.Suites, Failed: g.Failed
                        .Where(f => !unretriable.Contains(f.Test) && current[f.Test].Status == TestStatus.Failed)
                        .ToList()))
                    .Where(g => g.Failed.Count > 0)
                    .ToList();
                if (pending.Count == 0)
                {
                    break;
                }

                _state.RetryStatus = $"Retrying {pending.Sum(g => g.Failed.Count)} failed test(s): attempt {attempt} of {limit}";
                _state.NotifyChanged();
                log?.Write("Orchestrator", _state.RetryStatus);

                foreach (var (groupTargets, groupSuites, failed) in pending)
                {
                    string? filter;
                    try
                    {
                        filter = TestSelectionFilter.Build(
                            [.. failed.Select(f => f.Test.FullyQualifiedName).Distinct(StringComparer.Ordinal)],
                            groupSuites.SelectMany(s => s.Tests));
                    }
                    catch (ArgumentException ex)
                    {
                        log?.Write("Orchestrator", $"Not retrying: {ex.Message}");
                        filter = null;
                    }

                    if (filter is null)
                    {
                        unretriable.UnionWith(failed.Select(f => f.Test));
                        continue;
                    }

                    var result = await _testRunner.RunTestsAsync(
                        solutionPath, groupTargets, filter, collectCoverage: false, new TestRunCallbacks(), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();

                    var retried = result.Suites
                        .SelectMany(s => s.Tests.Select(t => (Suite: s.Name, Test: t)))
                        .Where(r => r.Test.Status is TestStatus.Passed or TestStatus.Failed)
                        .ToList();
                    var byKey = retried
                        .GroupBy(r => (r.Suite, r.Test.ResultKey))
                        .ToDictionary(g => g.Key, g => g.Last().Test);
                    // A retry that saw only one framework reports no framework, so match its results by name when
                    // only one failed test of that name was retried.
                    var byName = retried
                        .Where(r => r.Test.TargetFramework is null)
                        .GroupBy(r => (r.Suite, r.Test.FullyQualifiedName))
                        .ToDictionary(g => g.Key, g => g.Last().Test);
                    var uniqueNames = failed
                        .GroupBy(f => (f.Suite, f.Test.FullyQualifiedName))
                        .Where(g => g.Count() == 1)
                        .Select(g => g.Key)
                        .ToHashSet();
                    foreach (var (suite, original) in failed)
                    {
                        if (byKey.TryGetValue((suite, original.ResultKey), out var next)
                            || (uniqueNames.Contains((suite, original.FullyQualifiedName))
                                && byName.TryGetValue((suite, original.FullyQualifiedName), out next)))
                        {
                            var previous = current[original];
                            (earlier.TryGetValue(original, out var list) ? list : earlier[original] = [])
                                .Add(new TestAttempt(previous.Status, previous.Duration, previous.ErrorMessage, previous.StackTrace));
                            current[original] = next with { TargetFramework = original.TargetFramework };
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.Write("Orchestrator", $"Retrying failed tests stopped: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _state.RetryStatus = null;
            _state.NotifyChanged();
        }

        if (earlier.Count == 0)
        {
            return (suites, unretriable.Count > 0 ? "The failed tests could not be selected by a filter." : null);
        }

        return ([.. suites.Select(s => s with
        {
            Tests = [.. s.Tests.Select(t => earlier.TryGetValue(t, out var attempts)
                ? current[t] with { Attempts = attempts }
                : t)],
        })], null);
    }
}
