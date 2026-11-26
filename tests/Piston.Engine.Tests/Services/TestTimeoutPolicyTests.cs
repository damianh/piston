// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class TestTimeoutPolicyTests
{
    [Fact]
    public void HangThresholdIsHalfTheTimeoutByDefault()
    {
        var policy = new TestTimeoutPolicy(() => TimeSpan.FromMinutes(2));

        Assert.Equal(TimeSpan.FromMinutes(2), policy.Timeout);
        Assert.Equal(TimeSpan.FromMinutes(1), policy.HangThreshold);
    }

    [Fact]
    public void WithoutATimeoutTheHangThresholdIsTheDefault()
    {
        var policy = new TestTimeoutPolicy(() => null);

        Assert.Null(policy.Timeout);
        Assert.Equal(TestTimeoutPolicy.DefaultHangThreshold, policy.HangThreshold);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(90, 90)]
    public void AnExplicitHangThresholdWins(int seconds, int? expected)
    {
        var policy = new TestTimeoutPolicy(() => TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(seconds));

        Assert.Equal(expected is { } s ? TimeSpan.FromSeconds(s) : null, policy.HangThreshold);
    }

    [Fact]
    public void RefreshPicksUpChangesAndKeepsTheLastValueWhenTheProviderFails()
    {
        Func<TimeSpan?> current = () => TimeSpan.FromSeconds(30);
        var policy = new TestTimeoutPolicy(() => current());

        current = () => TimeSpan.FromSeconds(10);
        Assert.Equal(TimeSpan.FromSeconds(30), policy.Timeout);
        policy.Refresh();
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Timeout);

        current = () => throw new IOException("locked");
        policy.Refresh();
        Assert.Equal(TimeSpan.FromSeconds(10), policy.Timeout);
    }

    [Fact]
    public void ProviderFailingOnTheFirstRead_UsesTheDefaultTimeout()
    {
        var policy = new TestTimeoutPolicy(() => throw new IOException("locked"));

        Assert.Equal(TestTimeoutPolicy.DefaultTimeout, policy.Timeout);
    }
}
