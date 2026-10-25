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
/// <param name="Number">Position of the run in the session, from 1.</param>
/// <param name="EditedDuringRun">The source changed while the run executed, so its results are not current.</param>
public sealed record SessionTimelineRun(SessionTimelineEntryDto Entry, int Number, bool EditedDuringRun)
    : SessionTimelineItem(Entry.RequestedAt)
{
    public bool Started => Entry.SourceBefore is not null || Entry.StartedAt is not null;
}

/// <summary>Where a currently failing test's failure came from.</summary>
/// <param name="IntroducedBy">The run in which the test started failing in this session; null when it was failing
/// from the first run that ran it, or the run that introduced it is no longer retained.</param>
/// <param name="Edit">The edit the introducing run was the first to test; null when the run tested unchanged source.</param>
/// <param name="Classification">How the run history classified the failure, when history is on.</param>
public sealed record SessionFailureTrace(
    TestReference Test,
    SessionTimelineRun? IntroducedBy,
    SessionTimelineEdit? Edit,
    FailureClassificationDto? Classification);

/// <summary>The session's runs, with the edits between them derived from their source fingerprints.</summary>
public sealed class SessionTimeline
{
    private SessionTimeline(IReadOnlyList<SessionTimelineItem> items) => Items = items;

    /// <summary>Edits and runs, oldest first.</summary>
    public IReadOnlyList<SessionTimelineItem> Items { get; }

    public IEnumerable<SessionTimelineRun> Runs => Items.OfType<SessionTimelineRun>();

    public static SessionTimeline Empty { get; } = new([]);

    /// <summary>Orders the entries by request sequence and inserts an edit before each run that tested changed source.</summary>
    public static SessionTimeline Build(IReadOnlyList<SessionTimelineEntryDto> entries)
    {
        var items = new List<SessionTimelineItem>();
        string? lastSource = null;
        var pendingFiles = new List<string>();
        var pendingSeen = new HashSet<string>(StringComparer.Ordinal);
        var pendingUnlisted = 0;
        DateTimeOffset? pendingAt = null;
        var number = 0;

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
            items.Add(new SessionTimelineRun(entry, ++number, editedDuring));
        }

        return new SessionTimeline(items);
    }

    /// <summary>
    /// Traces each failing test to the run that started its current failing streak (the last run that newly failed it
    /// with no later run fixing it) and to the edit that produced the source that run tested.
    /// </summary>
    public IReadOnlyList<SessionFailureTrace> TraceFailures(IEnumerable<TestReference> failing)
    {
        var traces = new List<SessionFailureTrace>();
        foreach (var test in failing)
        {
            SessionTimelineRun? introducedBy = null;
            FailureClassificationDto? introducedAs = null;
            FailureClassificationDto? latest = null;
            foreach (var run in Runs)
            {
                var classification = run.Entry.FailureClassifications.FirstOrDefault(c => c.Project == test.Project && c.Test == test.Key);
                latest = classification ?? latest;
                if (run.Entry.FixedTests.Any(t => Matches(t, test)))
                {
                    introducedBy = null;
                    introducedAs = null;
                }

                if (run.Entry.NewlyFailingTests.Any(t => Matches(t, test)))
                {
                    introducedBy = run;
                    introducedAs = classification;
                }
            }

            var edit = introducedBy is null ? null : EditThatProduced(introducedBy);
            traces.Add(new SessionFailureTrace(test, introducedBy, edit, introducedBy is null ? latest : introducedAs ?? latest));
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
            if (changed || run.Entry.FailureClassifications.Any(c => c.Project == test.Project && c.Test == test.Key))
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

    private static bool Matches(TestOutcomeReferenceDto reference, TestReference test) =>
        reference.Project == test.Project && reference.Key == test.Key;
}
