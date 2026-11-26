// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Test execution preferences.</summary>
/// <param name="TimeoutSeconds">
/// How long a single test may run before Piston fails it, captures hang diagnostics and cancels its
/// project's test process. <c>0</c> turns the timeout off; null uses the built-in default
/// (<see cref="DefaultTimeoutSeconds"/>).
/// </param>
public sealed record TestSettings(double? TimeoutSeconds = null)
{
    /// <summary>The test timeout used when none is set.</summary>
    public const double DefaultTimeoutSeconds = 60;

    /// <summary>The largest accepted test timeout: one day.</summary>
    public const double MaxTimeoutSeconds = 24 * 60 * 60;

    /// <summary>
    /// The effective test timeout: <see cref="TimeoutSeconds"/> or the built-in default; null when
    /// the timeout is turned off.
    /// </summary>
    public static TimeSpan? ResolveTimeout(TestSettings? settings)
    {
        var seconds = settings?.TimeoutSeconds ?? DefaultTimeoutSeconds;
        return IsValid(seconds) && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;
    }

    /// <inheritdoc cref="UserSettings.Validate"/>
    public void Validate()
    {
        if (TimeoutSeconds is { } seconds && !IsValid(seconds))
        {
            throw new ArgumentException(
                $"Invalid value '{seconds}' for tests.timeoutSeconds; expected 0 (off) to {MaxTimeoutSeconds}.",
                nameof(TimeoutSeconds));
        }
    }

    /// <inheritdoc cref="UserSettings.WithoutInvalidValues"/>
    public TestSettings WithoutInvalidValues() =>
        TimeoutSeconds is { } seconds && !IsValid(seconds) ? this with { TimeoutSeconds = null } : this;

    private static bool IsValid(double seconds) =>
        double.IsFinite(seconds) && seconds >= 0 && seconds <= MaxTimeoutSeconds;
}
