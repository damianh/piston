// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>
/// The per-test timeout and the "possibly hanging" warning threshold. The timeout comes from a provider (the user
/// settings) and is read by <see cref="Refresh"/> at the start of each run, so a change applies from the next run.
/// The hang threshold is an explicit override or, by default, half the timeout.
/// </summary>
public sealed class TestTimeoutPolicy
{
    /// <summary>The test timeout used when no provider is given.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);

    /// <summary>The hang threshold used when the timeout is off and no threshold is configured.</summary>
    public static readonly TimeSpan DefaultHangThreshold = TimeSpan.FromSeconds(60);

    private readonly Func<TimeSpan?> _timeoutProvider;
    private readonly TimeSpan? _hangThresholdOverride;
    private readonly Lock _gate = new();
    private TimeSpan? _timeout;

    /// <param name="timeoutProvider">Returns the current test timeout, null when it is off. Null uses <see cref="DefaultTimeout"/>.</param>
    /// <param name="hangThresholdOverride">
    /// An explicit hang threshold; <see cref="TimeSpan.Zero"/> turns the warning off. Null derives it from the timeout.
    /// </param>
    public TestTimeoutPolicy(Func<TimeSpan?>? timeoutProvider = null, TimeSpan? hangThresholdOverride = null)
    {
        _timeoutProvider = timeoutProvider ?? (() => DefaultTimeout);
        _hangThresholdOverride = hangThresholdOverride;
        Refresh();
    }

    /// <summary>The test timeout read by the last <see cref="Refresh"/>; null when it is off.</summary>
    public TimeSpan? Timeout
    {
        get
        {
            lock (_gate)
            {
                return _timeout;
            }
        }
    }

    /// <summary>How long a test runs before it is reported as possibly hanging; null when the warning is off.</summary>
    public TimeSpan? HangThreshold => _hangThresholdOverride is { } explicitThreshold
        ? explicitThreshold > TimeSpan.Zero ? explicitThreshold : null
        : Timeout is { } timeout ? timeout / 2 : DefaultHangThreshold;

    /// <summary>Re-reads the test timeout; a provider that throws keeps the previous value.</summary>
    public void Refresh()
    {
        TimeSpan? timeout;
        try
        {
            timeout = _timeoutProvider();
        }
        catch (Exception ex)
        {
            DiagnosticLog.Instance?.Write("Timeout", $"Could not read the test timeout setting: {ex.Message}");
            return;
        }

        lock (_gate)
        {
            if (timeout != _timeout)
            {
                DiagnosticLog.Instance?.Write("Timeout",
                    $"Test timeout: {(timeout is { } t ? Durations.Format(t) : "off")}");
            }

            _timeout = timeout is { } value && value > TimeSpan.Zero ? value : null;
        }
    }
}
