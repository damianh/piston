// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

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
