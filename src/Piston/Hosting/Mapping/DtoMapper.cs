// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Engine.Coverage;
using Piston.Engine.Execution;
using Piston.Engine.Models;
using Piston.Hosting.Services;
using Piston.Mcp.Tools;
using Piston.Protocol.Dtos;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Mapping;

public static class DtoMapper
{
    public static MachineOverviewDto ToDto(this MachineExecutionOverview overview)
    {
        var running = overview.Running
            .Select(entry => entry.ToMachineEntryDto(overview.Owners))
            .ToList();
        var queue = overview.Queue
            .Select(entry => entry.ToMachineEntryDto(overview.Owners))
            .ToList();
        var worktrees = overview.Owners
            .Select(owner =>
            {
                var info = owner.Owner;
                var runningHere = info is not null && running.Any(entry => entry.ProcessId == info.ProcessId);
                var queuedHere = info is not null ? queue.FirstOrDefault(entry => entry.ProcessId == info.ProcessId) : null;
                var state = owner.Liveness == OwnershipLiveness.Stale ? "Stale"
                    : info is null || !overview.CoordinationAvailable ? "Unknown"
                    : runningHere ? "Running"
                    : queuedHere is not null ? "Queued"
                    : "Idle";
                return new MachineWorktreeDto(
                    owner.Key,
                    owner.Liveness.ToString(),
                    info?.ProcessId,
                    info?.SolutionPath,
                    info?.WorktreePath,
                    info?.AcquiredAt,
                    info?.Host,
                    info?.DashboardUrl,
                    info?.ProcessId == Environment.ProcessId,
                    state,
                    queuedHere?.QueuePosition);
            })
            .OrderBy(worktree => worktree.WorktreePath, StringComparer.Ordinal)
            .ToList();
        var budget = overview.Budget is { } value
            ? new MachineBudgetDto(
                value.CpuSlots, value.MemoryMb, overview.CpuSlotsInUse, overview.MemoryMbInUse,
                overview.CoordinationAvailable ? running.Count : null,
                overview.CoordinationAvailable ? queue.Count : null, value.Source)
            : null;

        return new MachineOverviewDto(
            overview.CapturedAt, budget, worktrees, running, queue, overview.Error,
            overview.CoordinationAvailable, overview.OwnersAvailable);
    }

    private static MachineExecutionEntryDto ToMachineEntryDto(
        this MachineExecutionEntry entry, IReadOnlyList<OwnershipRecord> owners)
    {
        var worktree = owners.FirstOrDefault(owner => owner.Key == entry.WorktreeKey)?.Owner?.WorktreePath;
        return new MachineExecutionEntryDto(
            entry.Sequence, entry.RunId, entry.ProcessId, entry.CpuSlots, entry.MemoryMb,
            entry.WorktreeKey, worktree, entry.QueuePosition);
    }

    public static ActivityEvent ToDto(this EngineActivityEvent activity)
    {
        var dto = activity switch
        {
            SolutionLoadedActivityEvent loaded =>
                ActivityEventFactory.SolutionLoaded(loaded.LoadedSolutionPath, loaded.ProjectCount),
            FileChangesDetectedActivityEvent changed =>
                ActivityEventFactory.FileChangesDetected(changed.SolutionPath, changed.ChangedFiles),
            BuildCompletedActivityEvent build =>
                ActivityEventFactory.BuildCompleted(build.SolutionPath, build.Succeeded, build.DurationMs, build.Errors, build.ErrorCount),
            TestRunCompletedActivityEvent tests =>
                ActivityEventFactory.TestRunCompleted(tests.SolutionPath, tests.Passed, tests.Failed, tests.Skipped,
                    tests.DurationMs, tests.Failures.Select(f => new TestFailureData(f.FullyQualifiedName, f.Message)).ToList()),
            _ => throw new ArgumentOutOfRangeException(nameof(activity), activity, "Unknown engine activity event."),
        };

        return dto with { Timestamp = activity.Timestamp };
    }

    public static TestStatusDto ToDto(this TestStatus status) => status switch
    {
        TestStatus.NotRun => TestStatusDto.NotRun,
        TestStatus.Running => TestStatusDto.Running,
        TestStatus.Passed => TestStatusDto.Passed,
        TestStatus.Failed => TestStatusDto.Failed,
        TestStatus.Skipped => TestStatusDto.Skipped,
        _ => TestStatusDto.NotRun,
    };

    public static BuildStatusDto ToDto(this BuildStatus status) => status switch
    {
        BuildStatus.None => BuildStatusDto.None,
        BuildStatus.Building => BuildStatusDto.Building,
        BuildStatus.Succeeded => BuildStatusDto.Succeeded,
        BuildStatus.Failed => BuildStatusDto.Failed,
        BuildStatus.Skipped => BuildStatusDto.Skipped,
        _ => BuildStatusDto.None,
    };

    public static PistonPhaseDto ToDto(this PistonPhase phase) => phase switch
    {
        PistonPhase.Idle => PistonPhaseDto.Idle,
        PistonPhase.Watching => PistonPhaseDto.Watching,
        PistonPhase.Analyzing => PistonPhaseDto.Analyzing,
        PistonPhase.Building => PistonPhaseDto.Building,
        PistonPhase.Testing => PistonPhaseDto.Testing,
        PistonPhase.Error => PistonPhaseDto.Error,
        _ => PistonPhaseDto.Idle,
    };

    public static ProjectRunStatusDto ToDto(this ProjectRunStatus status) => status switch
    {
        ProjectRunStatus.Pending => ProjectRunStatusDto.Pending,
        ProjectRunStatus.Running => ProjectRunStatusDto.Running,
        ProjectRunStatus.Completed => ProjectRunStatusDto.Completed,
        ProjectRunStatus.Failed => ProjectRunStatusDto.Failed,
        ProjectRunStatus.Crashed => ProjectRunStatusDto.Crashed,
        ProjectRunStatus.TimedOut => ProjectRunStatusDto.TimedOut,
        ProjectRunStatus.WaitingForResources => ProjectRunStatusDto.WaitingForResources,
        _ => ProjectRunStatusDto.Pending,
    };

    public static TestResultDto ToDto(this TestResult result) =>
        new(
            result.FullyQualifiedName,
            result.DisplayName,
            result.Status.ToDto(),
            result.Duration.TotalMilliseconds,
            result.Output,
            result.ErrorMessage,
            result.StackTrace,
            result.Source,
            result.TargetFramework,
            result.Attempts.Count == 0
                ? null
                : [.. result.Attempts.Select(a => new TestAttemptDto(a.Status.ToDto(), a.Duration.TotalMilliseconds, a.ErrorMessage, a.StackTrace))]
        )
        {
            ErrorOutput = result.ErrorOutput,
            Attachments = result.Attachments.Select(attachment => new TestAttachmentDto(
                attachment.RunId,
                attachment.Index,
                attachment.DisplayName,
                attachment.Description,
                attachment.OriginalPath is not null
                    ? Path.GetFileName(attachment.OriginalPath)
                    : attachment.Path is null ? null : Path.GetFileName(attachment.Path),
                attachment.SizeBytes,
                attachment.Retained,
                attachment.NotRetainedReason,
                attachment.Retained && attachment.RunId is not null
                    ? $"/artifacts/{Uri.EscapeDataString(attachment.RunId)}/{attachment.Index}"
                    : null)).ToList(),
        };

    public static TestSuiteDto ToDto(this TestSuite suite) =>
        new(
            suite.Name,
            suite.Tests.Select(t => t.ToDto()).ToList(),
            suite.Timestamp,
            suite.TotalDuration.TotalMilliseconds,
            suite.RunId,
            suite.Restored,
            suite.ProjectId
        );

    /// <summary>
    /// Most bytes a run log's entries may take once serialized. Half the named-pipe message limit leaves room
    /// for the rest of the response and its JSON-RPC envelope.
    /// </summary>
    private const long RunLogEntriesBudget = MessageFramer.DefaultMaxMessageSize / 2;

    // Upper bound for an entry's timestamp, property names and punctuation, and for an omitted marker.
    private const long LogEntryOverhead = 96;
    private const long OmittedMarkerAllowance = 256;

    public static ExecutionLogDto ToDto(this ExecutionLog log, string? runId)
    {
        var (entries, omitted) = FitToBudget(log.Entries, log.OmittedEntries, RunLogEntriesBudget);
        return new(
            log.ProjectPath,
            log.Runner,
            log.ExitCode,
            log.StartedAt,
            log.CompletedAt,
            omitted,
            [.. entries.Select(e => new ExecutionLogEntryDto(e.Timestamp, e.Category, e.Message))],
            runId
        );
    }

    /// <summary>
    /// Keeps as much of the start and end of <paramref name="entries"/> as fits in <paramref name="budget"/>
    /// serialized bytes, replacing the dropped middle, and any existing omitted marker, with one marker.
    /// </summary>
    private static (IReadOnlyList<ExecutionLogEntry> Entries, int Omitted) FitToBudget(
        IReadOnlyList<ExecutionLogEntry> entries, int omitted, long budget)
    {
        var sizes = new long[entries.Count];
        long total = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            sizes[i] = EstimateJsonSize(entries[i]);
            total += sizes[i];
        }

        if (total <= budget)
        {
            return (entries, omitted);
        }

        budget -= OmittedMarkerAllowance;

        // The dropped range must cover an existing marker so the result has only one.
        var marker = -1;
        for (var i = 0; i < entries.Count && marker < 0; i++)
        {
            if (string.Equals(entries[i].Category, ExecutionLog.OmittedCategory, StringComparison.Ordinal))
            {
                marker = i;
            }
        }

        var headLimit = marker >= 0 ? marker : entries.Count;
        var tailLimit = marker + 1;

        // Half the budget for the head, the rest for the tail, then any left over back to the head.
        long used = 0;
        var head = 0;
        while (head < headLimit && used + sizes[head] <= budget / 2)
        {
            used += sizes[head++];
        }

        var tail = entries.Count;
        while (tail > Math.Max(head, tailLimit) && used + sizes[tail - 1] <= budget)
        {
            used += sizes[--tail];
        }

        while (head < Math.Min(tail, headLimit) && used + sizes[head] <= budget)
        {
            used += sizes[head++];
        }

        // The existing marker in the dropped range stands for the entries it already counted.
        var dropped = tail - head + (marker >= 0 ? omitted - 1 : 0);
        var timestamp = entries[Math.Min(tail, entries.Count - 1)].Timestamp;

        List<ExecutionLogEntry> kept = new(head + 1 + entries.Count - tail);
        for (var i = 0; i < head; i++)
        {
            kept.Add(entries[i]);
        }

        kept.Add(ExecutionLog.OmittedMarker(timestamp, dropped));
        for (var i = tail; i < entries.Count; i++)
        {
            kept.Add(entries[i]);
        }

        return (kept, dropped);
    }

    private static long EstimateJsonSize(ExecutionLogEntry entry) =>
        LogEntryOverhead + EstimateJsonSize(entry.Category) + EstimateJsonSize(entry.Message);

    // Worst case: anything but plain printable ASCII may be written as a six-byte \uXXXX escape.
    private static long EstimateJsonSize(string value)
    {
        long size = 0;
        foreach (var c in value)
        {
            size += c is >= ' ' and <= '~' and not ('"' or '\\' or '<' or '>' or '&' or '\'' or '+' or '`') ? 1 : 6;
        }

        return size;
    }

    /// <summary>
    /// The log of the latest run of the test project identified by <paramref name="projectId"/> (one process covers
    /// all of its target frameworks), or <see langword="null"/> when none was recorded.
    /// </summary>
    public static ExecutionLogDto? FindRunLog(this PistonState state, string projectId)
    {
        // A selective run leaves the project's untouched results in the previous suite, so take the latest.
        var latest = state.TestSuites
            .Where(s => s.ExecutionLog is not null && string.Equals(s.Key, projectId, StringComparison.Ordinal))
            .MaxBy(s => s.Timestamp);

        return latest?.ExecutionLog!.ToDto(latest.RunId);
    }

    public static SourceFingerprintDto ToDto(this SourceFingerprint fingerprint) =>
        new(fingerprint.Algorithm, fingerprint.Value, fingerprint.FileCount);

    public static StoredRunSummaryDto ToDto(this StoredRunSummary run) => new(
        run.RunId, run.Trigger.ToString(), run.Status.ToString(), run.Scope?.ToString(), run.IsFullScope, run.Freshness.ToString(),
        run.RequestedAt, run.CompletedAt, run.Source, run.Passed, run.Failed, run.Skipped, run.Retried, run.PassedOnRetry);

    public static StoredRunDto ToDto(this StoredRun run) => new(
        run.Summary.ToDto(),
        [.. run.Results.Select(r => new StoredTestResultDto(r.Project, r.Test, r.Status.ToString(), r.DurationMs, r.ErrorMessage, r.StackTrace,
            r.Attempts is { Count: > 0 } attempts ? [.. attempts.Select(a => new StoredAttemptDto(a.Status.ToString(), a.DurationMs, a.ErrorMessage))] : null,
            r.ProjectId))]);

    public static TestHistoryDto ToDto(this TestHistory history) => new(
        history.Project, history.Test, history.Flaky,
        [.. history.Runs.Select(e => new TestHistoryEntryDto(e.RunId, e.At, e.Status.ToString(), e.DurationMs, e.Source, e.Attempt))],
        history.Flakiness?.ToDto(), history.ProjectId);

    public static FlakyEvidenceDto ToDto(this FlakinessEvidence evidence) => new(
        evidence.Source, evidence.Passed, evidence.Failed, evidence.RetryOutcomes, evidence.OtherWorktrees, evidence.Describe(),
        [.. evidence.Outcomes.Select(o => new FlakyOutcomeDto(o.RunId, o.At, o.Status.ToString(), o.Attempt, o.Worktree))]);

    /// <summary>One mark per test that is failing now or flaky.</summary>
    public static List<TestMarkDto> ToMarks(IReadOnlyList<FailureClassification> failures, IReadOnlyList<TestHistory> flaky)
    {
        var flakyByKey = flaky.GroupBy(f => (f.ProjectId, f.Test)).ToDictionary(g => g.Key, g => g.First().Flakiness);
        var marks = failures
            .Select(f =>
            {
                var evidence = f.Flakiness ?? flakyByKey.GetValueOrDefault((f.ProjectId, f.Test));
                return new TestMarkDto(f.Project, f.Test, f.Kind.ToString(), f.Evidence,
                    evidence is not null || flakyByKey.ContainsKey((f.ProjectId, f.Test)), evidence?.ToDto(), f.ProjectId);
            })
            .ToList();
        var failing = failures.Select(f => (f.ProjectId, f.Test)).ToHashSet();
        marks.AddRange(flaky.Where(f => !failing.Contains((f.ProjectId, f.Test)))
            .Select(f => new TestMarkDto(f.Project, f.Test, null, null, Flaky: true, f.Flakiness?.ToDto(), f.ProjectId)));
        return marks;
    }

    public static TestRunDto ToDto(this TestRunRecord run)
    {
        var tests = run.Tests.ToList();
        return new TestRunDto(
            RunId: run.RunId,
            EngineInstanceId: run.EngineInstanceId,
            Sequence: run.Sequence,
            Trigger: run.Trigger.ToString(),
            Status: run.Status.ToString(),
            StatusReason: run.StatusReason,
            Scope: run.Scope?.ToString(),
            IsFiltered: run.IsFiltered,
            IsFullScope: run.IsFullScope,
            Freshness: run.Freshness.ToString(),
            FreshnessReason: run.FreshnessReason,
            IsCurrentFullResult: run.IsCurrentFullResult,
            RequestedAt: run.RequestedAt,
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            SourceBefore: run.SourceBefore?.ToDto(),
            SourceAfter: run.SourceAfter?.ToDto(),
            SourceChangeObserved: run.SourceChangeObserved,
            EffectiveFilter: run.Context?.EffectiveFilter,
            TestProjects: run.Context?.TestProjects?.Select(path => ProjectId(run.Context.SolutionPath, path)).ToList(),
            TotalTests: tests.Count,
            Passed: tests.Count(t => t.Status == TestStatus.Passed),
            Failed: tests.Count(t => t.Status == TestStatus.Failed),
            Skipped: tests.Count(t => t.Status == TestStatus.Skipped),
            DuplicateTestCount: run.DuplicateTestIds.Count,
            Admission: run.Admission?.ToDto(),
            Build: run.Build?.ToDto(),
            SelectionExplanation: run.Context?.SelectionExplanation,
            SelectionReasons: run.Context?.SelectionReasons?.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyList<TestSelectionReasonDto>)[.. kv.Value.Select(r => r.ToDto())],
                StringComparer.Ordinal),
            ChangedFiles: run.ChangedFiles,
            FixedTests: [.. run.FixedTests.Select(t => new TestOutcomeReferenceDto(t.Project, t.Key) { ProjectId = t.ProjectId })],
            NewlyFailingTests: [.. run.NewlyFailingTests.Select(t => new TestOutcomeReferenceDto(t.Project, t.Key) { ProjectId = t.ProjectId })],
            Restored: run.Restored,
            ProjectResources: run.Projects.Any(p => p.Resources is not null || p.SuspectedResourceConflict is not null)
                ? [.. run.Projects
                    .Where(p => p.Resources is not null || p.SuspectedResourceConflict is not null)
                    .Select(p => p.ToResourcesDto())]
                : null,
            RetryLimit: run.RetryLimit,
            RetriesSkippedReason: run.RetriesSkippedReason,
            PassedOnRetry: tests.Count(t => t.Status == TestStatus.Passed && t.Attempts.Count > 0),
            Projects: [.. run.Projects.Select(project => new TestRunProjectOutcomeDto(
                project.ProjectPath,
                project.ProjectId,
                project.Status.ToString(),
                project.TestCount,
                project.NoTestsReason,
                project.TimedOut?.Test,
                project.TimedOut is { } timeout ? timeout.Limit.TotalMilliseconds : null))]);
    }

    public static TestProjectResourcesDto ToResourcesDto(this TestRunProjectOutcome project) =>
        new(
            ProjectPath: project.ProjectPath,
            Keys: project.Resources?.Needs.Keys ?? [],
            UsesContainers: project.Resources?.Needs.UsesContainers ?? false,
            Reasons: project.Resources?.Needs.Reasons ?? [],
            Wait: project.Resources?.Wait?.ToDto(),
            Warnings: project.Resources?.Warnings ?? [],
            SuspectedConflict: project.SuspectedResourceConflict);

    public static TestResourceWaitDto ToDto(this TestResourceWait wait) =>
        new(
            State: wait.State.ToString(),
            Keys: wait.Keys,
            UsesContainers: wait.UsesContainers,
            BlockedBy: [.. wait.BlockedBy.Select(b => new TestResourceBlockerDto(b.Resource, b.Kind.ToString(), b.ProcessId, b.Worktree, b.Project, b.Since))],
            QueuedAhead: wait.QueuedAhead,
            ContainerSlotsInUse: wait.ContainerSlotsInUse,
            ContainerSlotBudget: wait.ContainerSlotBudget,
            WaitingSince: wait.WaitingSince,
            AcquiredAt: wait.AcquiredAt,
            WaitedMs: wait.Waited?.TotalMilliseconds);

    /// <summary>
    /// Most bytes one page of the session timeline may take once serialized. Half the named-pipe message limit
    /// leaves room for the JSON-RPC envelope, and is well under the dashboard's WebSocket limit.
    /// </summary>
    internal const long SessionTimelinePageBudget = MessageFramer.DefaultMaxMessageSize / 2;

    /// <summary>
    /// A page of the session timeline: the runs requested after <paramref name="afterSequence"/>, in request order, up
    /// to <paramref name="budget"/> serialized bytes.
    /// </summary>
    /// <param name="inRunHistory">Whether a run is still kept in the run history, which retains fewer runs than the timeline.</param>
    public static SessionTimelineDto ToSessionTimelineDto(
        this PistonState state,
        long? afterSequence,
        Func<string, bool> inRunHistory,
        long budget = SessionTimelinePageBudget)
    {
        // Read the entries before the dropped count, so a concurrent trim overstates rather than hides dropped runs.
        var timeline = state.SessionTimeline;
        var dropped = state.SessionTimelineDroppedRuns;
        var entries = new List<SessionTimelineEntryDto>();
        long size = 0;
        var hasMore = false;
        foreach (var entry in timeline.Where(e => afterSequence is null || e.Sequence > afterSequence).OrderBy(e => e.Sequence))
        {
            var dto = entry.ToDto() with { InRunHistory = entry.InRunHistory && inRunHistory(entry.RunId) };
            var bytes = SerializedSize(dto);
            if (entries.Count > 0 && size + bytes > budget)
            {
                hasMore = true;
                break;
            }

            if (bytes > budget)
            {
                (dto, bytes) = TrimToBudget(dto, budget);
            }

            entries.Add(dto);
            size += bytes;
        }

        long? oldest = timeline.Count > 0 ? timeline.Min(e => e.Sequence) : null;
        return new(state.SessionStartedAt, state.SolutionPath, entries, hasMore, dropped, oldest);
    }

    /// <summary>
    /// Halves an oversized entry's lists until it fits. The totals are kept, so clients see the lists as truncated.
    /// </summary>
    private static (SessionTimelineEntryDto Entry, long Bytes) TrimToBudget(SessionTimelineEntryDto dto, long budget)
    {
        var keep = Math.Max(Math.Max(dto.ChangedFiles.Count, dto.NewlyFailingTests.Count), Math.Max(dto.FixedTests.Count, dto.FailureClassifications.Count));
        var bytes = SerializedSize(dto);
        while (bytes > budget && keep > 0)
        {
            keep /= 2;
            dto = dto with
            {
                ChangedFiles = [.. dto.ChangedFiles.Take(keep)],
                NewlyFailingTests = [.. dto.NewlyFailingTests.Take(keep)],
                FixedTests = [.. dto.FixedTests.Take(keep)],
                FailureClassifications = [.. dto.FailureClassifications.Take(keep)],
            };
            bytes = SerializedSize(dto);
        }

        return (dto, bytes);
    }

    private static long SerializedSize(SessionTimelineEntryDto dto) =>
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dto, JsonRpcSerializer.Options).LongLength;

    public static SessionTimelineEntryDto ToDto(this SessionTimelineEntry entry) => new(
        entry.RunId,
        entry.Sequence,
        entry.Trigger.ToString(),
        entry.Status.ToString(),
        entry.StatusReason,
        entry.Scope?.ToString(),
        entry.IsFullScope,
        entry.IsFiltered,
        entry.Freshness.ToString(),
        entry.FreshnessReason,
        entry.RequestedAt,
        entry.StartedAt,
        entry.CompletedAt,
        entry.SourceBefore,
        entry.SourceAfter,
        entry.SourceChangeObserved,
        entry.ChangedFiles,
        entry.ChangedFileCount,
        entry.BuildStatus?.ToString(),
        entry.BuildErrorCount,
        entry.Passed,
        entry.Failed,
        entry.Skipped,
        [.. entry.NewlyFailingTests.Select(t => new TestOutcomeReferenceDto(t.Project, t.Key) { ProjectId = t.ProjectId })],
        entry.NewlyFailingCount,
        [.. entry.FixedTests.Select(t => new TestOutcomeReferenceDto(t.Project, t.Key) { ProjectId = t.ProjectId })],
        entry.FixedCount,
        [.. entry.FailureClassifications.Select(c => new FailureClassificationDto(c.Project, c.Test, c.Kind.ToString(), c.Evidence, c.ProjectId))],
        entry.InRunHistory,
        entry.NoEarlierResults);

    public static TestSelectionReasonDto ToDto(this TestSelectionReason reason) =>
        new(reason.FilePath, reason.ChangedLines, reason.ExecutedLines, reason.Granularity, reason.ProjectPath, reason.Member);

    private static string ProjectId(string solutionPath, string projectPath)
    {
        var solutionDirectory = Path.GetDirectoryName(Path.GetFullPath(solutionPath)) ?? Path.GetFullPath(solutionPath);
        return Path.GetRelativePath(solutionDirectory, Path.GetFullPath(projectPath, solutionDirectory)).Replace('\\', '/');
    }

    public static CoveringTestsDto ToCoveringTestsDto(string filePath, IReadOnlyList<CoveringTest>? tests) =>
        new(filePath, [.. (tests ?? []).Select(t => new CoveringTestDto(
            t.TestFqn,
            t.ProjectPath,
            t.Granularity,
            [.. t.ExecutedLines.Select(r => new LineRangeDto(r.Start, r.End))],
            t.IsCurrent))]);

    /// <summary>The changed-line coverage reply when coverage is disabled.</summary>
    public static ChangedLineCoverageDto DisabledChangedLineCoverage { get; } =
        new(false, null, new ChangedLineTotalsDto(0, 0, 0, 0, 0, 0), new Dictionary<string, int>(), [], 0, []);

    /// <summary>
    /// Most bytes the changed files may take once serialized. Half the named-pipe message limit leaves room for
    /// the rest of the reply and its JSON-RPC envelope.
    /// </summary>
    internal const long ChangedLineFilesBudget = MessageFramer.DefaultMaxMessageSize / 2;

    /// <summary>
    /// Maps the verdicts to line ranges per file. Files are added in path order while they fit in
    /// <paramref name="budget"/> serialized bytes: a file whose detail does not fit keeps only its totals, and once
    /// even that does not fit the remaining files are omitted. The reply's totals always count every line.
    /// </summary>
    internal static ChangedLineCoverageDto ToDto(
        this ChangedLineCoverageResult result, string solutionDirectory, long budget = ChangedLineFilesBudget)
    {
        var files = new List<ChangedLineFileDto>();
        var detailOmitted = 0;
        long used = 0;
        foreach (var file in result.Files)
        {
            var dto = file.ToDto(solutionDirectory, withDetail: true);
            var size = SerializedSize(dto);
            if (used + size > budget)
            {
                dto = file.ToDto(solutionDirectory, withDetail: false);
                size = SerializedSize(dto);
                if (used + size > budget)
                {
                    break;
                }

                detailOmitted++;
            }

            files.Add(dto);
            used += size;
        }

        var filesOmitted = result.Files.Count - files.Count;
        var warnings = result.Warnings.ToList();
        if (filesOmitted > 0 || detailOmitted > 0)
        {
            warnings.Add(
                $"The reply is too large to list every changed line: {detailOmitted} file(s) give only totals and " +
                $"{filesOmitted} file(s) are omitted. The totals count every changed line.");
        }

        return new(
            Enabled: true,
            Base: result.Basis,
            Totals: result.Totals.ToDto(),
            UnknownReasons: result.UnknownReasons.ToDictionary(
                kv => ChangedLineVerdicts.Name(kv.Key)!, kv => kv.Value, StringComparer.Ordinal),
            Files: files,
            FilesOmitted: filesOmitted,
            Warnings: warnings);

        static long SerializedSize(ChangedLineFileDto dto) =>
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(dto, JsonRpcSerializer.Options).Length + 1;
    }

    private static ChangedLineFileDto ToDto(this ChangedFileVerdicts file, string solutionDirectory, bool withDetail)
    {
        var failing = file.FailingTests();
        var failingListed = withDetail ? Math.Min(failing.Count, CoverageTools.MaxFailingTestsPerFile) : 0;
        return new(
            file.FilePath,
            Path.GetRelativePath(solutionDirectory, file.FilePath).Replace('\\', '/'),
            file.IsMeasured,
            file.IsStale,
            file.IsPartial,
            file.Totals.ToDto(),
            CoveredByPassing: Ranges(ChangedLineVerdict.CoveredByPassing),
            CoveredOnlyByFailing: Ranges(ChangedLineVerdict.CoveredOnlyByFailing),
            Uncovered: Ranges(ChangedLineVerdict.Uncovered),
            NonExecutable: Ranges(ChangedLineVerdict.NonExecutable),
            Unknown: withDetail
                ? [.. file.Lines
                    .Where(l => l.Verdict == ChangedLineVerdict.Unknown)
                    .GroupBy(l => l.Reason)
                    .OrderBy(g => g.Min(l => l.Line))
                    .Select(g => new ChangedLineUnknownDto(ChangedLineVerdicts.Name(g.Key)!, LineRanges(g.Select(l => l.Line))))]
                : [],
            PartialBranches: withDetail
                ? [.. file.Lines.Where(l => l.HasPartialBranches)
                    .Select(l => new ChangedLineBranchesDto(l.Line, l.Coverage!.BranchesCovered, l.Coverage.BranchesTotal))]
                : [],
            FailingTests: [.. failing.Take(failingListed)],
            FailingTestsOmitted: failing.Count - failingListed,
            DetailOmitted: !withDetail);

        List<LineRangeDto> Ranges(ChangedLineVerdict verdict) =>
            withDetail ? LineRanges(file.Lines.Where(l => l.Verdict == verdict).Select(l => l.Line)) : [];
    }

    /// <summary>Collapses line numbers into ordered, inclusive ranges of consecutive lines.</summary>
    private static List<LineRangeDto> LineRanges(IEnumerable<int> lines)
    {
        var ranges = new List<LineRangeDto>();
        int? start = null, previous = null;
        foreach (var line in lines.Order())
        {
            if (previous is { } p && line <= p + 1)
            {
                previous = Math.Max(p, line);
                continue;
            }

            if (start is { } s)
            {
                ranges.Add(new LineRangeDto(s, previous!.Value));
            }

            start = previous = line;
        }

        if (start is { } last)
        {
            ranges.Add(new LineRangeDto(last, previous!.Value));
        }

        return ranges;
    }

    private static ChangedLineTotalsDto ToDto(this ChangedLineTotals totals) =>
        new(totals.ChangedLines, totals.CoveredByPassing, totals.CoveredOnlyByFailing, totals.Uncovered,
            totals.NonExecutable, totals.Unknown);

    public static TestRunAdmissionDto ToDto(this TestRunAdmission admission) =>
        new(
            State: admission.State.ToString(),
            ReservedCpuSlots: admission.ReservedCpuSlots,
            ReservedMemoryMb: admission.ReservedMemoryMb,
            BudgetCpuSlots: admission.BudgetCpuSlots,
            BudgetMemoryMb: admission.BudgetMemoryMb,
            BudgetSource: admission.BudgetSource,
            QueuedAhead: admission.QueuedAhead,
            QueueLength: admission.QueueLength,
            OtherAdmittedRuns: admission.OtherAdmittedRuns,
            CpuSlotsInUse: admission.CpuSlotsInUse,
            MemoryMbInUse: admission.MemoryMbInUse,
            QueuedAt: admission.QueuedAt,
            AdmittedAt: admission.AdmittedAt);

    public static BuildResultDto ToDto(this BuildResult result) =>
        new(
            result.Status.ToDto(),
            result.Errors,
            result.Warnings,
            result.Duration.TotalMilliseconds,
            result.OmittedErrors,
            result.OmittedWarnings
        );

    public static StateSnapshotNotification ToSnapshot(this PistonState state) =>
        new(
            Phase: state.Phase.ToDto(),
            Suites: state.TestSuites.Select(s => s.ToDto()).ToList(),
            InProgressSuites: state.InProgressSuites.Select(s => s.ToDto()).ToList(),
            LastBuild: state.LastBuild?.ToDto(),
            LastRunTime: state.LastRunTime,
            LastBuildDurationMs: state.LastBuildDuration?.TotalMilliseconds,
            LastTestDurationMs: state.LastTestDuration?.TotalMilliseconds,
            LastTestRunnerError: state.LastTestRunnerError,
            TotalExpectedTests: state.TotalExpectedTests,
            CompletedTests: state.CompletedTests,
            VerifiedSinceChangeCount: state.VerifiedSinceChangeCount,
            LastFileChangeTime: state.LastFileChangeTime,
            SolutionPath: state.SolutionPath,
            AffectedProjects: state.AffectedProjects,
            AffectedTestProjects: state.AffectedTestProjects,
            LastChangedFiles: state.LastChangedFiles,
            CoverageEnabled: state.CoverageEnabled,
            HasCoverageData: state.HasCoverageData,
            CoverageImpactDetail: state.CoverageImpactDetail,
            TotalTestProjects: state.TotalTestProjects,
            CompletedTestProjects: state.CompletedTestProjects,
            ProjectStatuses: state.ProjectStatuses.Count > 0
                ? state.ProjectStatuses.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value.ToDto())
                : null,
            LastRun: state.LastRun?.ToDto(),
            ActiveRun: state.CurrentRun?.ToDto(),
            TotalBuildProjects: state.TotalBuildProjects,
            CompletedBuildProjects: state.CompletedBuildProjects,
            LastBuiltProject: state.LastBuiltProject,
            ImpactBackfill: state.ImpactBackfill is { } backfill
                ? new ImpactBackfillDto(backfill.Granularity, backfill.Completed, backfill.Total, backfill.CurrentUnit, backfill.ProjectPath)
                : null,
            LongestRunningTest: state.LongestRunningTest is { } running
                ? new RunningTestDto(running.Name, running.ProjectPath, running.StartedAt, running.PossiblyHanging)
                : null,
            ProjectResourceWaits: state.ProjectResourceWaits.Count > 0
                ? state.ProjectResourceWaits.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToDto())
                : null,
            RetryStatus: state.RetryStatus,
            TimelineRevision: state.SessionTimelineRevision
        );

    public static CoverageSummaryDto ToCoverageSummaryDto(CoverageReportSnapshot? report, string? solutionDirectory) =>
        report is null
            ? new CoverageSummaryDto(false, null, 0, 0, 0, 0, [])
            : new CoverageSummaryDto(
                Enabled: true,
                GeneratedAt: report.GeneratedAt,
                LinesTotal: report.LinesTotal,
                LinesCovered: report.LinesCovered,
                BranchesTotal: report.BranchesTotal,
                BranchesCovered: report.BranchesCovered,
                Files: [.. report.Files.Select(f => f.ToDto(solutionDirectory))]);

    public static CoverageFileSummaryDto ToDto(this CoverageFileSummary file, string? solutionDirectory) => new(
        FilePath: file.FilePath,
        RelativePath: solutionDirectory is null
            ? file.FilePath
            : Path.GetRelativePath(solutionDirectory, file.FilePath).Replace('\\', '/'),
        Module: file.Module,
        LinesTotal: file.LinesTotal,
        LinesCovered: file.LinesCovered,
        BranchesTotal: file.BranchesTotal,
        BranchesCovered: file.BranchesCovered,
        IsStale: file.IsStale,
        IsPartial: file.IsPartial,
        UpdatedAt: file.UpdatedAt);

    public static CoverageLineDto ToDto(this LineCoverage line) => new(
        line.LineNumber,
        line.HitCount,
        line.HitCount == 0 ? "uncovered"
            : line.BranchesTotal > 0 && line.BranchesCovered < line.BranchesTotal ? "partial"
            : "covered",
        line.BranchesCovered,
        line.BranchesTotal);

    public static FileCoverageDto ToFileCoverageDto(
        string requestedPath, FileCoverageSnapshot? coverage, string? solutionDirectory, bool includeSource) =>
        coverage is null
            ? new FileCoverageDto(requestedPath, [])
            : new FileCoverageDto(
                coverage.File.FilePath,
                [.. coverage.Lines.Select(l => l.ToDto())],
                coverage.File.ToDto(solutionDirectory),
                includeSource ? SourceFileReader.Read(coverage.File.FilePath, solutionDirectory).Lines : null)
            {
                StaleLines = [.. coverage.StaleLines.Select(range => new LineRangeDto(range.Start, range.End))],
            };
}
