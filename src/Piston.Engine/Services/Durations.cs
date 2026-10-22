// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;

namespace Piston.Engine.Services;

/// <summary>Parses and formats the durations of settings such as the hang threshold and the test timeout.</summary>
public static class Durations
{
    /// <summary>
    /// Parses seconds (<c>90</c>) or a number with a unit (<c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>1.5h</c>).
    /// <c>0</c> or <c>off</c> turns the setting off (<paramref name="value"/> is null).
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan? value)
    {
        value = null;
        var s = text?.Trim().ToLowerInvariant() ?? "";
        if (s is "off" or "0")
        {
            return true;
        }

        var (number, unit) = s switch
        {
            _ when s.EndsWith("ms", StringComparison.Ordinal) => (s[..^2], 0.001),
            _ when s.EndsWith('s') => (s[..^1], 1.0),
            _ when s.EndsWith('m') => (s[..^1], 60.0),
            _ when s.EndsWith('h') => (s[..^1], 3600.0),
            _ => (s, 1.0),
        };

        if (!double.TryParse(number.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            || double.IsInfinity(amount * unit))
        {
            return false;
        }

        if (amount > 0)
        {
            value = TimeSpan.FromSeconds(amount * unit);
        }

        return true;
    }

    /// <summary>Formats a duration for messages: <c>500 ms</c>, <c>30 s</c>, <c>5 min</c>, <c>1.5 h</c>.</summary>
    public static string Format(TimeSpan duration) => duration switch
    {
        { TotalSeconds: < 1 } => $"{duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms",
        { TotalMinutes: < 1 } => $"{duration.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} s",
        { TotalHours: < 1 } => $"{duration.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} min",
        _ => $"{duration.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h",
    };
}
