// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A page of the terminal runs since the daemon's engine started, in request order.</summary>
/// <param name="StartedAt">When the session began.</param>
/// <param name="SolutionPath">The watched solution.</param>
/// <param name="HasMore">More entries follow; request them after the last entry's sequence.</param>
/// <param name="EarlierRunsDropped">How many of the session's oldest runs are no longer retained.</param>
/// <param name="OldestRetainedSequence">
/// The sequence of the oldest retained run; entries loaded from earlier pages with a lower sequence are no longer retained.
/// </param>
public sealed record SessionTimelineDto(
    DateTimeOffset StartedAt,
    string? SolutionPath,
    IReadOnlyList<SessionTimelineEntryDto> Entries,
    bool HasMore = false,
    int EarlierRunsDropped = 0,
    long? OldestRetainedSequence = null);
