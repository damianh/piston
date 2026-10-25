// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>An edit or a run on the session timeline.</summary>
public abstract record SessionTimelineItem(DateTimeOffset At);

/// <summary>
/// The tested source changed between two runs: the source fingerprint a run started from differs from the one the
/// previous run started from.
/// </summary>
/// <param name="FromSource">Fingerprint the previous run tested; null when this is the first run that captured one.</param>
/// <param name="ToSource">Fingerprint <paramref name="NextRunId"/> tested.</param>
/// <param name="Files">Files reported changed since the previous run started, including by runs superseded meanwhile.</param>
/// <param name="FileCount">How many files changed, including any beyond <paramref name="Files"/>.</param>
/// <param name="NextRunId">The first run that tested the edited source.</param>
public sealed record SessionTimelineEdit(
    DateTimeOffset At,
    string? FromSource,
    string ToSource,
    IReadOnlyList<string> Files,
    int FileCount,
    string NextRunId) : SessionTimelineItem(At);

/// <summary>A terminal run of the session.</summary>
/// <param name="Number">The engine's request sequence for the run, from 1; stable however the runs finish or are retained.</param>
/// <param name="EditedDuringRun">The source changed while the run executed, so its results are not current.</param>
public sealed record SessionTimelineRun(SessionTimelineEntryDto Entry, long Number, bool EditedDuringRun)
    : SessionTimelineItem(Entry.RequestedAt)
{
    public bool Started => Entry.SourceBefore is not null || Entry.StartedAt is not null;
}

/// <summary>What the retained timeline establishes about where a failure came from.</summary>
public enum FailureOrigin
{
    /// <summary>A retained run is known to have started the current failing streak.</summary>
    Run,

    /// <summary>No run of the session changed the test from passing to failing: it failed in the session's first results.</summary>
    SessionStart,

    /// <summary>
    /// The retained data cannot say: the introducing run was dropped from the timeline, or a run's capped outcome
    /// lists may have omitted the test.
    /// </summary>
    Unknown,
}

/// <summary>Where a currently failing test's failure came from.</summary>
/// <param name="Origin">What the timeline establishes; <paramref name="IntroducedBy"/> is set only for <see cref="FailureOrigin.Run"/>.</param>
/// <param name="IntroducedBy">The run in which the test started failing in this session.</param>
/// <param name="Edit">The edit that produced the source the introducing run tested; null when none is recorded.</param>
/// <param name="SourceUnchanged">No edit is recorded because the introducing run tested the source the session started with.</param>
/// <param name="Classification">How the run history classified the failure, when history is on.</param>
public sealed record SessionFailureTrace(
    TestReference Test,
    FailureOrigin Origin,
    SessionTimelineRun? IntroducedBy,
    SessionTimelineEdit? Edit,
    FailureClassificationDto? Classification,
    bool SourceUnchanged = false);

/// <summary>The session's runs, with the edits between them derived from their source fingerprints.</summary>
public sealed class SessionTimeline
{
    private SessionTimeline(IReadOnlyList<SessionTimelineItem> items, int earlierRunsDropped)
    {
        Items = items;
        EarlierRunsDropped = earlierRunsDropped;
    }

    /// <summary>Edits and runs, oldest first.</summary>
    public IReadOnlyList<SessionTimelineItem> Items { get; }

    public IEnumerable<SessionTimelineRun> Runs => Items.OfType<SessionTimelineRun>();

    /// <summary>How many of the session's oldest runs are no longer retained.</summary>
    public int EarlierRunsDropped { get; }

    public static SessionTimeline Empty { get; } = new([], 0);

    /// <summary>Orders the entries by request sequence and inserts an edit before each run that tested changed source.</summary>
    /// <param name="earlierRunsDropped">How many of the session's oldest runs precede <paramref name="entries"/> but are no longer retained.</param>
    public static SessionTimeline Build(IReadOnlyList<SessionTimelineEntryDto> entries, int earlierRunsDropped = 0)
    {
        var items = new List<SessionTimelineItem>();
        string? lastSource = null;
        var pendingFiles = new List<string>();
        var pendingSeen = new HashSet<string>(StringComparer.Ordinal);
        var pendingUnlisted = 0;
        DateTimeOffset? pendingAt = null;

        foreach (var entry in entries.OrderBy(e => e.Sequence))
        {
            if (entry.ChangedFileCount > 0)
            {
                pendingAt ??= entry.RequestedAt;
                foreach (var file in entry.ChangedFiles)
                {
                    if (pendingSeen.Add(file))
                    {
                        pendingFiles.Add(file);
                    }
                }

                pendingUnlisted += Math.Max(0, entry.ChangedFileCount - entry.ChangedFiles.Count);
            }

            // Only a run that captured its starting fingerprint says which source was tested; files changed for runs
            // superseded before they started carry over to the next run that did.
            if (entry.SourceBefore is { } source)
            {
                if (source != lastSource && (lastSource is not null || pendingFiles.Count > 0))
                {
                    items.Add(new SessionTimelineEdit(
                        pendingAt ?? entry.StartedAt ?? entry.RequestedAt,
                        lastSource,
                        source,
                        [.. pendingFiles],
                        pendingFiles.Count + pendingUnlisted,
                        entry.RunId));
                }

                lastSource = source;
                pendingFiles.Clear();
                pendingSeen.Clear();
                pendingUnlisted = 0;
                pendingAt = null;
            }

            var editedDuring = entry.Freshness == "Stale"
                || (entry.SourceAfter is not null && entry.SourceBefore is not null && entry.SourceAfter != entry.SourceBefore);
            items.Add(new SessionTimelineRun(entry, entry.Sequence, editedDuring));
        }

        return new SessionTimeline(items, earlierRunsDropped);
    }

    /// <summary>
    /// Traces each failing test to the run that started its current failing streak (the last run that newly failed it
    /// with no later run fixing it) and to the edit that produced the source that run tested. Outcome lists are capped,
    /// so a test missing from a truncated list may still have changed there; when that leaves the streak's start
    /// uncertain, or it may precede the retained runs, the origin is <see cref="FailureOrigin.Unknown"/>.
    /// </summary>
    public IReadOnlyList<SessionFailureTrace> TraceFailures(IEnumerable<TestReference> failing)
    {
        var traces = new List<SessionFailureTrace>();
        foreach (var test in failing)
        {
            SessionTimelineRun? introducedBy = null;
            FailureClassificationDto? introducedAs = null;
            FailureClassificationDto? latest = null;

            // A listed fix means the current streak started later. An unlisted fix (a truncated fixed list) only
            // matters if an unlisted new failure (a truncated newly failing list) could follow it. A run with no
            // earlier results to compare with after the session already had some (results were cleared) lists no
            // changes at all, as if both its lists were truncated.
            var fixedSinceStreak = false;
            var possiblyFixedUnlisted = false;
            var uncertain = false;
            var sawResults = EarlierRunsDropped > 0;
            foreach (var run in Runs)
            {
                var entry = run.Entry;
                var classification = entry.FailureClassifications.FirstOrDefault(c => Matches(c, test));
                var blind = entry.NoEarlierResults && sawResults;
                var fixedUnlisted = blind || entry.FixedCount > entry.FixedTests.Count;
                var failingUnlisted = blind || entry.NewlyFailingCount > entry.NewlyFailingTests.Count;
                if (entry.FixedTests.Any(t => Matches(t, test)))
                {
                    introducedBy = null;
                    introducedAs = null;
                    latest = null;
                    fixedSinceStreak = true;
                    possiblyFixedUnlisted = false;
                    uncertain = false;
                }
                else if (entry.NewlyFailingTests.Any(t => Matches(t, test)))
                {
                    // A new streak doesn't inherit an earlier streak's classification.
                    introducedBy = run;
                    introducedAs = classification;
                    latest = classification;
                    fixedSinceStreak = false;
                    possiblyFixedUnlisted = false;
                    uncertain = false;
                }
                else
                {
                    latest = classification ?? latest;

                    // A test can't be fixed and newly fail in the same run, so only an earlier unlisted fix counts.
                    if (failingUnlisted && (introducedBy is null || possiblyFixedUnlisted))
                    {
                        uncertain = true;
                    }

                    possiblyFixedUnlisted |= fixedUnlisted;
                }

                sawResults |= entry.Passed + entry.Failed + entry.Skipped > 0;
            }

            var origin = introducedBy is not null
                ? uncertain ? FailureOrigin.Unknown : FailureOrigin.Run
                : uncertain || fixedSinceStreak || EarlierRunsDropped > 0 ? FailureOrigin.Unknown : FailureOrigin.SessionStart;
            if (origin != FailureOrigin.Run)
            {
                introducedBy = null;
            }

            var edit = introducedBy is null ? null : EditThatProduced(introducedBy);
            var sourceUnchanged = edit is null && introducedBy?.Entry.SourceBefore is not null && EarlierRunsDropped == 0;
            traces.Add(new SessionFailureTrace(
                test, origin, introducedBy, edit, introducedBy is null ? latest : introducedAs ?? latest, sourceUnchanged));
        }

        return traces;
    }

    /// <summary>The latest edit before <paramref name="run"/> that produced the source it tested; null when none did.</summary>
    public SessionTimelineEdit? EditThatProduced(SessionTimelineRun run)
    {
        if (run.Entry.SourceBefore is not { } source)
        {
            return null;
        }

        var index = -1;
        for (var i = 0; i < Items.Count && index < 0; i++)
        {
            index = ReferenceEquals(Items[i], run) ? i : -1;
        }

        for (var i = index - 1; i >= 0; i--)
        {
            if (Items[i] is SessionTimelineEdit edit)
            {
                return edit.ToSource == source ? edit : null;
            }
        }

        return null;
    }

    /// <summary>
    /// What to highlight when following a test: the runs that newly failed it, fixed it or classified its failure, and
    /// the edits that produced the source of the runs that changed its outcome.
    /// </summary>
    public (HashSet<string> Runs, HashSet<SessionTimelineEdit> Edits) Related(TestReference test)
    {
        var runs = new HashSet<string>(StringComparer.Ordinal);
        var edits = new HashSet<SessionTimelineEdit>();
        foreach (var run in Runs)
        {
            var changed = run.Entry.NewlyFailingTests.Any(t => Matches(t, test)) || run.Entry.FixedTests.Any(t => Matches(t, test));
            if (changed || run.Entry.FailureClassifications.Any(c => Matches(c, test)))
            {
                _ = runs.Add(run.Entry.RunId);
            }

            if (changed && EditThatProduced(run) is { } edit)
            {
                _ = edits.Add(edit);
            }
        }

        return (runs, edits);
    }

    /// <summary>Whether <paramref name="reference"/> is <paramref name="test"/>, matched by project ID.</summary>
    public static bool Matches(TestOutcomeReferenceDto reference, TestReference test) =>
        reference.ProjectId == test.Project && reference.Key == test.Key;

    /// <summary>Whether <paramref name="classification"/> is for <paramref name="test"/>, matched by project ID.</summary>
    public static bool Matches(FailureClassificationDto classification, TestReference test) =>
        (classification.ProjectId ?? classification.Project) == test.Project && classification.Test == test.Key;
}
