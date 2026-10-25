// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Piston.Protocol.JsonRpc;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Services;

/// <summary>
/// Bounded, thread-safe history of the activity events broadcast by the daemon,
/// so clients that connect late (e.g. a freshly loaded dashboard) can backfill the timeline.
/// </summary>
/// <param name="capacity">Most events retained.</param>
/// <param name="byteBudget">Most serialized bytes <see cref="Snapshot"/> returns.</param>
public sealed class ActivityHistory(int capacity = ActivityHistory.DefaultCapacity, long byteBudget = ActivityHistory.DefaultByteBudget)
{
    public const int DefaultCapacity = 500;

    /// <summary>Half the named-pipe message limit, leaving room for the JSON-RPC envelope.</summary>
    public const long DefaultByteBudget = MessageFramer.DefaultMaxMessageSize / 2;

    private readonly Queue<(ActivityEvent Event, long Size)> _events = new(capacity);
    private readonly Lock _lock = new();

    public void Add(ActivityEvent activity)
    {
        var size = JsonSerializer.SerializeToUtf8Bytes(activity, JsonRpcSerializer.Options).LongLength;
        lock (_lock)
        {
            if (_events.Count >= capacity)
            {
                _ = _events.Dequeue();
            }

            _events.Enqueue((activity, size));
        }
    }

    /// <summary>
    /// Returns the newest retained events that fit in the byte budget, in chronological order (oldest first).
    /// The timeline stays contiguous: once an event does not fit, older events are omitted too.
    /// </summary>
    public IReadOnlyList<ActivityEvent> Snapshot()
    {
        (ActivityEvent Event, long Size)[] retained;
        lock (_lock)
        {
            retained = [.. _events];
        }

        long total = 0;
        var first = retained.Length;
        while (first > 0)
        {
            // One byte per event for the separating comma.
            var size = retained[first - 1].Size + 1;
            if (total + size > byteBudget)
            {
                break;
            }

            total += size;
            first--;
        }

        return [.. retained[first..].Select(e => e.Event)];
    }
}
