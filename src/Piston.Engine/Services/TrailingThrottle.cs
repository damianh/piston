// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>
/// Trailing-edge throttle: <see cref="Signal"/> invokes the action immediately when the interval has
/// elapsed since the last invocation, otherwise schedules one invocation for the end of the interval,
/// so the latest state is always delivered. Invocations are serialized; the action should read current
/// state when invoked so a slower invocation can never publish an older snapshot after a newer one.
/// </summary>
internal sealed class TrailingThrottle : IDisposable
{
    private readonly Action _action;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly object _invokeGate = new();
    private ITimer? _timer;
    private DateTimeOffset? _lastInvoke;
    private bool _disposed;

    public TrailingThrottle(Action action, TimeSpan interval, TimeProvider? timeProvider = null)
    {
        _action = action;
        _interval = interval;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Requests an invocation, now or at the end of the current interval.</summary>
    public void Signal()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            var now = _time.GetUtcNow();
            var elapsed = _lastInvoke is { } last ? now - last : _interval;
            if (elapsed < _interval)
            {
                _timer = _time.CreateTimer(_ => OnTimer(), null, _interval - elapsed, Timeout.InfiniteTimeSpan);
                return;
            }

            _lastInvoke = now;
        }

        Invoke();
    }

    /// <summary>Cancels any scheduled invocation and invokes the action now.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CancelTimer();
            _lastInvoke = _time.GetUtcNow();
        }

        Invoke();
    }

    private void OnTimer()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CancelTimer();
            _lastInvoke = _time.GetUtcNow();
        }

        Invoke();
    }

    private void Invoke()
    {
        lock (_invokeGate)
        {
            if (!Volatile.Read(ref _disposed))
            {
                _action();
            }
        }
    }

    // Caller holds _gate.
    private void CancelTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>Cancels any scheduled invocation and waits for an in-flight one to finish.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            CancelTimer();
        }

        lock (_invokeGate)
        {
        }
    }
}
