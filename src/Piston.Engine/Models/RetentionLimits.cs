// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

internal static class RetentionLimits
{
    public const int MaxTestTextLength = 64 * 1024;
    public const int MaxActivityFailures = 50;
    public const int MaxActivityBuildErrors = 50;
    public const int MaxActivityMessageLength = 1_000;
    public const int MaxBuildDiagnostics = 500;
    public const int MaxBuildDiagnosticLength = 2_000;
    public const int MaxDiagnosticLogLineLength = 4_000;

    public static string? TruncateHead(string? text, int maxLength) =>
        text is null ? null : TruncateHead(text, text.Length, maxLength);

    internal static string TruncateHead(string text, long originalLength, int maxLength)
    {
        if (originalLength <= maxLength)
        {
            return text;
        }

        var retained = Math.Min(text.Length, maxLength);
        string marker;
        for (var i = 0; i < 4; i++)
        {
            marker = TruncationMarker(originalLength - retained);
            var next = Math.Max(0, maxLength - marker.Length);
            if (next == retained)
            {
                return text[..Math.Min(retained, text.Length)] + marker;
            }

            retained = next;
        }

        marker = TruncationMarker(originalLength - retained);
        retained = Math.Min(retained, Math.Max(0, maxLength - marker.Length));
        marker = TruncationMarker(originalLength - retained);
        return text[..Math.Min(retained, text.Length)] + marker;
    }

    public static string? TruncateHeadTail(string? text, int maxLength)
    {
        if (text is null || text.Length <= maxLength)
        {
            return text;
        }

        return TruncateHeadTail(text, text, text.Length, maxLength);
    }

    internal static string TruncateHeadTail(string headSource, string tailSource, long originalLength, int maxLength)
    {
        var marker = TruncationMarker(originalLength - maxLength);
        var headLength = 0;
        var tailLength = 0;
        for (var i = 0; i < 4; i++)
        {
            var retained = Math.Max(0, maxLength - marker.Length);
            headLength = (retained + 1) / 2;
            tailLength = retained - headLength;
            var next = TruncationMarker(originalLength - headLength - tailLength);
            if (next.Length == marker.Length)
            {
                marker = next;
                break;
            }

            marker = next;
        }

        var head = headSource[..Math.Min(headLength, headSource.Length)];
        var tail = tailLength == 0 ? string.Empty : tailSource[^Math.Min(tailLength, tailSource.Length)..];
        return string.Concat(head, marker, tail);
    }

    internal static string TruncationMarker(long count) => $"… [{count} characters truncated] …";
}
