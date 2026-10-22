// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;

namespace Piston.Engine.Impact;

/// <summary>An inclusive, 1-based range of source lines.</summary>
internal readonly record struct LineSpan(int Start, int End)
{
    public bool Overlaps(LineSpan other) => Start <= other.End && other.Start <= End;

    public override string ToString() => Start == End
        ? Start.ToString(CultureInfo.InvariantCulture)
        : string.Create(CultureInfo.InvariantCulture, $"{Start}-{End}");

    /// <summary>Sorts <paramref name="spans"/> and merges overlapping or adjacent spans.</summary>
    public static IReadOnlyList<LineSpan> Normalize(IEnumerable<LineSpan> spans)
    {
        var merged = new List<LineSpan>();
        foreach (var span in spans.Where(s => s.Start <= s.End).OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && span.Start <= merged[^1].End + 1)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, span.End) };
            }
            else
            {
                merged.Add(span);
            }
        }

        return merged;
    }

    /// <summary>Formats spans compactly for storage, e.g. <c>3-9,12,20-31</c>.</summary>
    public static string Format(IEnumerable<LineSpan> spans)
    {
        var builder = new StringBuilder();
        foreach (var span in spans)
        {
            if (builder.Length > 0)
            {
                _ = builder.Append(',');
            }

            _ = builder.Append(span.ToString());
        }

        return builder.ToString();
    }

    /// <summary>Parses the output of <see cref="Format"/>; malformed parts are skipped.</summary>
    public static IReadOnlyList<LineSpan> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var spans = new List<LineSpan>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = part.IndexOf('-', StringComparison.Ordinal);
            if (dash < 0)
            {
                if (int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
                {
                    spans.Add(new LineSpan(line, line));
                }
            }
            else if (int.TryParse(part.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                && int.TryParse(part.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                && start <= end)
            {
                spans.Add(new LineSpan(start, end));
            }
        }

        return spans;
    }
}
