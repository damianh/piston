// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

internal sealed class ProvenanceTestRunner : ITestRunnerService
{
    public Action? DuringRun { get; set; }
    public Func<string, IReadOnlyList<TestAttachment>>? Attachments { get; set; }
    public string? FailingTest { get; set; }
    public bool DuplicateFirstTest { get; set; }
    public HashSet<string> ProjectsWithoutResults { get; } = [];
    public HashSet<string> CrashedProjects { get; } = [];
    public Dictionary<string, TestTimeout> TimedOutProjects { get; } = [];
    public string? RunningTest { get; set; }
    public TimeSpan HoldRunning { get; set; }
    public string? LastFilter { get; private set; }
    public bool AttachExecutionLog { get; set; }

    /// <summary>How many more runs <see cref="FailingTest"/> fails in before it passes; null fails it every time.</summary>
    public int? FailuresRemaining { get; set; }

    /// <summary>The framework unfiltered runs report for <see cref="FailingTest"/>, as a multi-targeted project's would.</summary>
    public string? FailingTestFramework { get; set; }

    /// <summary>Extra failing tests each project reports.</summary>
    public int ExtraFailures { get; set; }

    /// <summary>Every call: its targets, filter and whether it collected coverage.</summary>
    public List<(IReadOnlyList<string>? Targets, string? Filter, bool Coverage)> Calls { get; } = [];

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath, string? filter, Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
        RunTestsAsync(solutionPath, null, filter, false, onProgress, null, ct);

    public Task<TestRunResult> RunTestsAsync(
        string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress, CancellationToken ct) =>
        RunTestsAsync(solutionPath, testProjectPaths, filter, collectCoverage, onProgress, null, ct);

    public async Task<TestRunResult> RunTestsAsync(
        string solutionPath, IReadOnlyList<string>? testProjectPaths, string? filter, bool collectCoverage,
        Action<IReadOnlyList<TestSuite>>? onProgress, Action<ProjectTestResult>? onProjectCompleted, CancellationToken ct)
    {
        LastFilter = filter;
        Calls.Add((testProjectPaths, filter, collectCoverage));
        DuringRun?.Invoke();
        var failing = FailingTest is not null && FailuresRemaining is not <= 0;
        if (failing && FailuresRemaining is { } remaining && testProjectPaths?.Any(p => FailingTest!.StartsWith(Path.GetFileNameWithoutExtension(p) + ".", StringComparison.Ordinal)) == true)
        {
            FailuresRemaining = remaining - 1;
        }

        if (RunningTest is { } running)
        {
            onProgress?.Invoke([new TestSuite("A.Tests",
                [new TestResult(running, running, TestStatus.Running, TimeSpan.Zero, null, null, null, null)],
                DateTimeOffset.UtcNow, TimeSpan.Zero)]);
            await Task.Delay(HoldRunning, ct);
        }

        var all = new List<TestSuite>();
        var projects = new List<ProjectTestResult>();
        void Complete(ProjectTestResult project)
        {
            projects.Add(project);
            onProjectCompleted?.Invoke(project);
        }

        foreach (var path in testProjectPaths ?? [])
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (CrashedProjects.Contains(path))
            {
                Complete(new ProjectTestResult(path, [], "crashed", [], Crashed: true));
                continue;
            }
            if (TimedOutProjects.TryGetValue(path, out var timeout))
            {
                var timedOutTest = new TestResult(timeout.Test, timeout.Test, TestStatus.Failed, timeout.Limit, null, "Timed out", null, null);
                var timedOutSuite = new TestSuite(name, [timedOutTest], DateTimeOffset.UtcNow, timeout.Limit);
                all.Add(timedOutSuite);
                Complete(new ProjectTestResult(path, [timedOutSuite], null, [], Crashed: false) { TimedOut = timeout });
                continue;
            }
            if (ProjectsWithoutResults.Contains(path))
            {
                Complete(new ProjectTestResult(path, [], null, [], Crashed: false));
                continue;
            }

            var fqn = $"{name}.Test1";
            var tests = new List<TestResult>
            {
                new(fqn, "Test1", failing && fqn == FailingTest ? TestStatus.Failed : TestStatus.Passed,
                    TimeSpan.FromMilliseconds(1), null, failing && fqn == FailingTest ? "boom" : null, null, null)
                {
                    TargetFramework = filter is null && fqn == FailingTest ? FailingTestFramework : null,
                    Attachments = Attachments?.Invoke(path) ?? [],
                },
            };
            for (var i = 0; i < ExtraFailures; i++)
            {
                tests.Add(new($"{name}.Extra{i}", $"Extra{i}", TestStatus.Failed, TimeSpan.FromMilliseconds(1), null, "boom", null, null));
            }

            if (DuplicateFirstTest && all.Count == 0)
            {
                tests.Add(tests[0]);
            }

            var suite = new TestSuite(name, tests, DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(1))
            {
                ExecutionLog = AttachExecutionLog ? new ExecutionLog(path) : null,
            };
            all.Add(suite);
            Complete(new ProjectTestResult(path, [suite], null, [], Crashed: false));
        }

        return new TestRunResult(all, null, []) { Projects = projects };
    }
}
