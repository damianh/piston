// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>
/// The single region in which a file differs from a recorded snapshot, found by trimming the lines the two
/// versions share at the start and the end. Several separate edits produce one region spanning all of them,
/// which over-selects but never misses a change.
/// </summary>
/// <param name="Prefix">Number of leading lines both versions share.</param>
/// <param name="OldCount">Number of snapshot lines replaced (0 for a pure insertion).</param>
/// <param name="NewCount">Number of current lines replacing them (0 for a pure deletion).</param>
internal readonly record struct SnapshotDiff(int Prefix, int OldCount, int NewCount)
{
    /// <summary>
    /// The snapshot lines the change touches. A pure insertion touches the lines on either side of it, so
    /// code added inside an executed method overlaps that method.
    /// </summary>
    public LineSpan OldRange => OldCount > 0
        ? new LineSpan(Prefix + 1, Prefix + OldCount)
        : new LineSpan(Math.Max(1, Prefix), Prefix + 1);

    /// <summary>Returns null when the two versions are identical.</summary>
    public static SnapshotDiff? Compute(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines)
    {
        var prefix = 0;
        var max = Math.Min(oldLines.Count, newLines.Count);
        while (prefix < max && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        if (prefix == oldLines.Count && prefix == newLines.Count)
        {
            return null;
        }

        var suffix = 0;
        while (suffix < max - prefix
            && string.Equals(oldLines[oldLines.Count - 1 - suffix], newLines[newLines.Count - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        return new SnapshotDiff(prefix, oldLines.Count - prefix - suffix, newLines.Count - prefix - suffix);
    }

    /// <summary>
    /// Maps spans recorded against the snapshot onto the current version: lines before the change keep their
    /// numbers, lines after it shift, and a span touching the change grows to cover all of the new lines.
    /// </summary>
    public IReadOnlyList<LineSpan> Remap(IEnumerable<LineSpan> spans)
    {
        var delta = NewCount - OldCount;
        var mapped = new List<LineSpan>();
        foreach (var span in spans)
        {
            if (span.End <= Prefix)
            {
                mapped.Add(span);
            }
            else if (span.Start > Prefix + OldCount)
            {
                mapped.Add(new LineSpan(span.Start + delta, span.End + delta));
            }
            else
            {
                // The span contains replaced lines or straddles an insertion point.
                var start = Math.Min(span.Start, Prefix + 1);
                var end = Math.Max(span.End + delta, Prefix + Math.Max(NewCount, 1));
                mapped.Add(new LineSpan(start, end));
            }
        }

        return LineSpan.Normalize(mapped);
    }

    /// <summary>Splits text into lines, treating <c>\r\n</c>, <c>\n</c> and <c>\r</c> alike.</summary>
    public static string[] SplitLines(string text) => text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None);
}
