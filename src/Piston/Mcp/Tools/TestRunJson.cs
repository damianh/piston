// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;
using Piston.Engine.Models;

namespace Piston.Mcp.Tools;

/// <summary>Serializes a <see cref="TestRunRecord"/> as the <c>piston.test-run/v1</c> JSON document.</summary>
public static class TestRunJson
{
    public const string Schema = "piston.test-run/v1";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static string Serialize(TestRunRecord run) => ToJson(run).ToJsonString(WriteOptions);

    public static JsonObject ToJson(TestRunRecord run)
    {
        var tests = run.Suites
            .SelectMany(suite => suite.Tests.Select(test => (ProjectId: suite.Key, Test: test)))
            .ToList();
        var testArray = new JsonArray();
        foreach (var (projectId, t) in tests
                     .OrderBy(item => item.ProjectId, StringComparer.Ordinal)
                     .ThenBy(item => item.Test.FullyQualifiedName, StringComparer.Ordinal)
                     .ThenBy(item => item.Test.TargetFramework, StringComparer.Ordinal))
        {
            testArray.Add(new JsonObject
            {
                ["projectId"] = projectId,
                ["fullyQualifiedName"] = t.FullyQualifiedName,
                ["targetFramework"] = t.TargetFramework,
                ["displayName"] = t.DisplayName,
                ["status"] = t.Status.ToString(),
                ["durationMs"] = Math.Round(t.Duration.TotalMilliseconds, 3),
                ["errorMessage"] = t.ErrorMessage,
                ["attempts"] = t.Attempts.Count == 0 ? null : new JsonArray(t.Attempts
                    .Select(a => (JsonNode)new JsonObject
                    {
                        ["status"] = a.Status.ToString(),
                        ["durationMs"] = Math.Round(a.Duration.TotalMilliseconds, 3),
                        ["errorMessage"] = a.ErrorMessage,
                    })
                    .ToArray()),
            });
        }

        return new JsonObject
        {
            ["schema"] = Schema,
            ["runId"] = run.RunId,
            ["solutionPath"] = run.SolutionPath ?? run.Context?.SolutionPath,
            ["engineInstanceId"] = run.EngineInstanceId,
            ["sequence"] = run.Sequence,
            ["trigger"] = run.Trigger.ToString(),
            ["status"] = run.Status.ToString(),
            ["statusReason"] = run.StatusReason,
            ["isTerminal"] = run.IsTerminal,
            ["scope"] = run.Scope?.ToString(),
            ["isFiltered"] = run.IsFiltered,
            ["isFullScope"] = run.IsFullScope,
            ["freshness"] = run.Freshness.ToString(),
            ["freshnessReason"] = run.FreshnessReason,
            ["isCurrentFullResult"] = run.IsCurrentFullResult,
            ["restored"] = run.Restored,
            ["requestedAt"] = run.RequestedAt.ToString("O"),
            ["startedAt"] = run.StartedAt?.ToString("O"),
            ["completedAt"] = run.CompletedAt?.ToString("O"),
            ["sourceBefore"] = Fingerprint(run.SourceBefore),
            ["sourceAfter"] = Fingerprint(run.SourceAfter),
            ["sourceChangeObserved"] = run.SourceChangeObserved,
            ["context"] = Context(run.Context),
            ["build"] = run.Build is null ? null : new JsonObject
            {
                ["status"] = run.Build.Status.ToString(),
                ["errors"] = Strings(run.Build.Errors),
                ["warnings"] = Strings(run.Build.Warnings),
                ["omittedErrors"] = run.Build.OmittedErrors,
                ["omittedWarnings"] = run.Build.OmittedWarnings,
                ["durationMs"] = Math.Round(run.Build.Duration.TotalMilliseconds, 3),
            },
            ["projects"] = new JsonArray(run.Projects
                .Select(p => (JsonNode)new JsonObject
                {
                    ["projectPath"] = p.ProjectPath,
                    ["projectId"] = p.ProjectId,
                    ["status"] = p.Status.ToString(),
                    ["testCount"] = p.TestCount,
                    ["noTestsReason"] = p.NoTestsReason,
                    ["timedOutTest"] = p.TimedOut?.Test,
                    ["testTimeoutMs"] = p.TimedOut is { } timeout ? Math.Round(timeout.Limit.TotalMilliseconds, 3) : null,
                    ["resources"] = Resources(p.Resources),
                    ["suspectedResourceConflict"] = p.SuspectedResourceConflict,
                })
                .ToArray()),
            ["runnerError"] = run.RunnerError,
            ["retryLimit"] = run.RetryLimit,
            ["retriesSkippedReason"] = run.RetriesSkippedReason,
            ["admission"] = Admission(run.Admission),
            ["summary"] = new JsonObject
            {
                ["total"] = tests.Count,
                ["passed"] = tests.Count(t => t.Test.Status == TestStatus.Passed),
                ["failed"] = tests.Count(t => t.Test.Status == TestStatus.Failed),
                ["skipped"] = tests.Count(t => t.Test.Status == TestStatus.Skipped),
                ["passedOnRetry"] = tests.Count(t => t.Test.Status == TestStatus.Passed && t.Test.Attempts.Count > 0),
                ["other"] = tests.Count(t => t.Test.Status is not (TestStatus.Passed or TestStatus.Failed or TestStatus.Skipped)),
            },
            ["duplicateTestIds"] = new JsonArray(run.DuplicateTestIds.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["failureClassifications"] = new JsonArray(run.FailureClassifications
                .Select(c => (JsonNode)new JsonObject
                {
                    ["project"] = c.Project,
                    ["projectId"] = c.ProjectId,
                    ["test"] = c.Test,
                    ["kind"] = c.Kind.ToString(),
                    ["evidence"] = c.Evidence,
                    ["flakiness"] = Flakiness(c.Flakiness),
                })
                .ToArray()),
            ["possiblyHangingTests"] = new JsonArray(run.PossiblyHangingTests.Select(d => (JsonNode)JsonValue.Create(d)!).ToArray()),
            ["changedFiles"] = Strings(run.ChangedFiles),
            ["fixedTests"] = OutcomeReferences(run.FixedTests),
            ["newlyFailingTests"] = OutcomeReferences(run.NewlyFailingTests),
            ["tests"] = testArray,
        };
    }

    private static JsonArray OutcomeReferences(IReadOnlyList<TestOutcomeReference> tests) =>
        new(tests.Select(t => (JsonNode)new JsonObject { ["project"] = t.Project, ["projectId"] = t.ProjectId, ["key"] = t.Key }).ToArray());

    internal static JsonObject? Flakiness(FlakinessEvidence? f) => f is null
        ? null
        : new JsonObject
        {
            ["source"] = f.Source,
            ["passed"] = f.Passed,
            ["failed"] = f.Failed,
            ["retryOutcomes"] = f.RetryOutcomes,
            ["otherWorktrees"] = Strings(f.OtherWorktrees),
            ["outcomes"] = new JsonArray(f.Outcomes
                .Select(o => (JsonNode)new JsonObject
                {
                    ["runId"] = o.RunId,
                    ["at"] = o.At.ToString("O"),
                    ["status"] = o.Status.ToString(),
                    ["attempt"] = o.Attempt,
                    ["worktree"] = o.Worktree,
                })
                .ToArray()),
        };

    private static JsonObject? Fingerprint(SourceFingerprint? fp) => fp is null
        ? null
        : new JsonObject
        {
            ["algorithm"] = fp.Algorithm,
            ["value"] = fp.Value,
            ["fileCount"] = fp.FileCount,
        };

    private static JsonObject? Admission(TestRunAdmission? a) => a is null
        ? null
        : new JsonObject
        {
            ["state"] = a.State.ToString(),
            ["reservedCpuSlots"] = a.ReservedCpuSlots,
            ["reservedMemoryMb"] = a.ReservedMemoryMb,
            ["budgetCpuSlots"] = a.BudgetCpuSlots,
            ["budgetMemoryMb"] = a.BudgetMemoryMb,
            ["budgetSource"] = a.BudgetSource,
            ["queuedAhead"] = a.QueuedAhead,
            ["queueLength"] = a.QueueLength,
            ["otherAdmittedRuns"] = a.OtherAdmittedRuns,
            ["cpuSlotsInUse"] = a.CpuSlotsInUse,
            ["memoryMbInUse"] = a.MemoryMbInUse,
            ["queuedAt"] = a.QueuedAt.ToString("O"),
            ["admittedAt"] = a.AdmittedAt?.ToString("O"),
        };

    private static JsonObject? Resources(TestProjectResources? r) => r is null
        ? null
        : new JsonObject
        {
            ["keys"] = Strings(r.Needs.Keys),
            ["usesContainers"] = r.Needs.UsesContainers,
            ["reasons"] = Strings(r.Needs.Reasons),
            ["wait"] = ResourceWait(r.Wait),
            ["warnings"] = Strings(r.Warnings),
        };

    internal static JsonObject? ResourceWait(TestResourceWait? w) => w is null
        ? null
        : new JsonObject
        {
            ["state"] = w.State.ToString(),
            ["keys"] = Strings(w.Keys),
            ["usesContainers"] = w.UsesContainers,
            ["blockedBy"] = new JsonArray(w.BlockedBy.Select(b => (JsonNode)new JsonObject
            {
                ["resource"] = b.Resource,
                ["kind"] = b.Kind.ToString(),
                ["processId"] = b.ProcessId,
                ["worktree"] = b.Worktree,
                ["project"] = b.Project,
                ["since"] = b.Since.ToString("O"),
            }).ToArray()),
            ["queuedAhead"] = w.QueuedAhead,
            ["containerSlotsInUse"] = w.ContainerSlotsInUse,
            ["containerSlotBudget"] = w.ContainerSlotBudget,
            ["waitingSince"] = w.WaitingSince.ToString("O"),
            ["acquiredAt"] = w.AcquiredAt?.ToString("O"),
            ["waitedMs"] = w.Waited is { } waited ? Math.Round(waited.TotalMilliseconds, 3) : null,
        };

    private static JsonObject? Context(TestRunContext? c) => c is null
        ? null
        : new JsonObject
        {
            ["solutionPath"] = c.SolutionPath,
            ["testProjects"] = c.TestProjects is null
                ? null
                : Strings(c.TestProjects.Select(path => ProjectId(c.SolutionPath, path)).ToList()),
            ["buildTargets"] = Strings(c.BuildTargets),
            ["userFilter"] = c.UserFilter,
            ["effectiveFilter"] = c.EffectiveFilter,
            ["coverageSelectedTestCount"] = c.CoverageSelectedTestCount,
            ["coverageEnabled"] = c.CoverageEnabled,
            ["buildConfiguration"] = c.BuildConfiguration,
            ["sdkVersion"] = c.SdkVersion,
            ["runtime"] = c.Runtime,
            ["operatingSystem"] = c.OperatingSystem,
            ["workingDirectory"] = c.WorkingDirectory,
            ["testWorkingDirectories"] = c.TestWorkingDirectories is null ? null
                : new JsonObject(c.TestWorkingDirectories.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(pair.Value)))),
            ["selectionExplanation"] = c.SelectionExplanation,
            ["selectionReasons"] = c.SelectionReasons is null ? null
                : new JsonObject(c.SelectionReasons.Select(pair =>
                    new KeyValuePair<string, JsonNode?>(pair.Key, new JsonArray(pair.Value.Select(reason => (JsonNode)new JsonObject
                    {
                        ["filePath"] = reason.FilePath,
                        ["changedLines"] = reason.ChangedLines,
                        ["executedLines"] = reason.ExecutedLines,
                        ["granularity"] = reason.Granularity,
                        ["projectPath"] = reason.ProjectPath,
                    }).ToArray())))),
        };

    private static JsonArray? Strings(IReadOnlyList<string>? values) => values is null
        ? null
        : new JsonArray(values.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray());

    private static string ProjectId(string solutionPath, string projectPath)
    {
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Path.GetFullPath(solutionPath);
        return Path.GetRelativePath(solutionDirectory, Path.GetFullPath(projectPath, solutionDirectory)).Replace('\\', '/');
    }
}
