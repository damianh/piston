// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>A terminal run of the session.</summary>
/// <param name="Number">The engine's request sequence for the run, from 1; stable however the runs finish or are retained.</param>
/// <param name="EditedDuringRun">The source changed while the run executed, so its results are not current.</param>
public sealed record SessionTimelineRun(SessionTimelineEntryDto Entry, long Number, bool EditedDuringRun)
    : SessionTimelineItem(Entry.RequestedAt)
{
    public bool Started => Entry.SourceBefore is not null || Entry.StartedAt is not null;
}
