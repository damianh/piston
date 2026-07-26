using Piston.Engine;
using Piston.Protocol.Messages;

namespace Piston.Cli.Services;

/// <summary>
/// Tracks MCP tool calls in a fixed-size ring buffer (last 200 entries).
/// Emits <see cref="ActivityEvent"/> notifications via <see cref="IActivityEventSink"/>.
/// </summary>
internal sealed class McpCallTracker(IActivityEventSink sink, string? solutionPath) : IMcpCallRecorder
{
    private const int RingBufferCapacity = 200;

    private readonly IActivityEventSink _sink = sink;
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
                _count++;
        }

        _sink.Emit(ActivityEventFactory.McpToolCall(_solutionPath, toolName, paramsSummary, resultSummary, durationMs, succeeded));
    }

    /// <summary>
    /// Returns a snapshot of the ring buffer in chronological order (oldest first).
    /// </summary>
    public IReadOnlyList<McpToolCallData> GetCallLog()
    {
        lock (_lock)
        {
            if (_count == 0)
                return [];

            var result = new McpToolCallData[_count];
            var start = _count < RingBufferCapacity ? 0 : _head;

            for (var i = 0; i < _count; i++)
                result[i] = _buffer[(start + i) % RingBufferCapacity];

            return result;
        }
    }
}
