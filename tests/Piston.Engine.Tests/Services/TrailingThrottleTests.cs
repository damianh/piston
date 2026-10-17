// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TrailingThrottleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    [Fact]
    public void FirstSignal_InvokesImmediately()
    {
        var count = 0;
        using var sut = new TrailingThrottle(() => count++, Interval, new ManualTimeProvider());

        sut.Signal();

        Assert.Equal(1, count);
    }

    [Fact]
    public void SignalsWithinTheInterval_CoalesceIntoOneTrailingInvocation()
    {
        var time = new ManualTimeProvider();
        var count = 0;
        using var sut = new TrailingThrottle(() => count++, Interval, time);

        sut.Signal();
        time.Advance(TimeSpan.FromMilliseconds(30));
        sut.Signal();
        sut.Signal();
        Assert.Equal(1, count);

        time.Advance(TimeSpan.FromMilliseconds(69));
        Assert.Equal(1, count);
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(2, count);

        time.Advance(Interval);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Flush_InvokesNowAndCancelsTheScheduledInvocation()
    {
        var time = new ManualTimeProvider();
        var count = 0;
        using var sut = new TrailingThrottle(() => count++, Interval, time);

        sut.Signal();
        sut.Signal();
        sut.Flush();
        Assert.Equal(2, count);

        time.Advance(Interval);
        Assert.Equal(2, count);
    }

    [Fact]
    public void Dispose_CancelsTheScheduledInvocation()
    {
        var time = new ManualTimeProvider();
        var count = 0;
        var sut = new TrailingThrottle(() => count++, Interval, time);

        sut.Signal();
        sut.Signal();
        sut.Dispose();
        time.Advance(Interval);
        sut.Signal();
        sut.Flush();

        Assert.Equal(1, count);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private TimeSpan _elapsed;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, _elapsed + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            _elapsed += by;
            foreach (var timer in _timers.Where(t => !t.Disposed && t.DueAt <= _elapsed).ToArray())
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueAt) : ITimer
        {
            public TimeSpan DueAt => dueAt;
            public bool Disposed { get; private set; }

            public void Fire()
            {
                Disposed = true;
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
