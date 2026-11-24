// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Where a test project's run stands in the machine-wide test resource queue.</summary>
/// <param name="State">Waiting or acquired.</param>
/// <param name="Keys">The exclusive resources the run needs.</param>
/// <param name="UsesContainers">Whether the project uses containers.</param>
/// <param name="BlockedBy">Runs holding or queued ahead for a needed resource; empty once acquired.</param>
/// <param name="QueuedAhead">Earlier queued runs that conflict with this one.</param>
/// <param name="WaitingSince">When the run asked for its resources.</param>
/// <param name="AcquiredAt">When it got them, or null while waiting.</param>
public sealed record TestResourceWait(
    TestResourceWaitState State,
    IReadOnlyList<string> Keys,
    bool UsesContainers,
    IReadOnlyList<TestResourceBlocker> BlockedBy,
    int QueuedAhead,
    DateTimeOffset WaitingSince,
    DateTimeOffset? AcquiredAt)
{
    /// <summary>How long the run waited, once acquired.</summary>
    public TimeSpan? Waited => AcquiredAt - WaitingSince;

    internal bool SameAs(TestResourceWait? other) =>
        other is not null
        && State == other.State
        && QueuedAhead == other.QueuedAhead
        && BlockedBy.SequenceEqual(other.BlockedBy);
}
