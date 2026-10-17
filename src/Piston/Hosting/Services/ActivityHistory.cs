// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Messages;

namespace Piston.Hosting.Services;

/// <summary>
/// Bounded, thread-safe history of the activity events broadcast by the daemon,
/// so clients that connect late (e.g. a freshly loaded dashboard) can backfill the timeline.
/// </summary>
public sealed class ActivityHistory(int capacity = ActivityHistory.DefaultCapacity)
{
    public const int DefaultCapacity = 500;

    private readonly Queue<ActivityEvent> _events = new(capacity);
    private readonly Lock _lock = new();

    public void Add(ActivityEvent activity)
    {
        lock (_lock)
        {
            if (_events.Count >= capacity)
            {
                _ = _events.Dequeue();
            }

            _events.Enqueue(activity);
        }
    }

    /// <summary>Returns the retained events in chronological order (oldest first).</summary>
    public IReadOnlyList<ActivityEvent> Snapshot()
    {
        lock (_lock)
        {
            return [.. _events];
        }
    }
}
