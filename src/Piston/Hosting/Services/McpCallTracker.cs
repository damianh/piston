// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine;
using Piston.Protocol.Messages;

namespace Piston.Hosting.Services;

/// <summary>
/// Tracks MCP tool calls in a fixed-size ring buffer (last 200 entries).
/// Emits wire-level <see cref="ActivityEvent"/> notifications.
/// </summary>
public sealed class McpCallTracker(Action<ActivityEvent> emit, string? solutionPath) : IMcpCallRecorder
{
    private const int RingBufferCapacity = 200;

    private readonly Action<ActivityEvent> _emit = emit;
    private readonly string? _solutionPath = solutionPath;
    private readonly McpToolCallData[] _buffer = new McpToolCallData[RingBufferCapacity];
    private readonly Lock _lock = new();
    private int _head;
    private int _count;

    /// <summary>
    /// Records a completed MCP tool call and emits an activity event.
    /// </summary>
    public void Record(string toolName, string? paramsSummary, string? resultSummary, double durationMs, bool succeeded)
    {
        var entry = new McpToolCallData(toolName, paramsSummary, resultSummary, durationMs, succeeded);

        lock (_lock)
        {
            _buffer[_head] = entry;
            _head = (_head + 1) % RingBufferCapacity;
            if (_count < RingBufferCapacity)
            {
                _count++;
            }
        }

        _emit(ActivityEventFactory.McpToolCall(_solutionPath, toolName, paramsSummary, resultSummary, durationMs, succeeded));
    }

    /// <summary>
    /// Returns a snapshot of the ring buffer in chronological order (oldest first).
    /// </summary>
    public IReadOnlyList<McpToolCallData> GetCallLog()
    {
        lock (_lock)
        {
            if (_count == 0)
            {
                return [];
            }

            var result = new McpToolCallData[_count];
            var start = _count < RingBufferCapacity ? 0 : _head;

            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % RingBufferCapacity];
            }

            return result;
        }
    }
}
