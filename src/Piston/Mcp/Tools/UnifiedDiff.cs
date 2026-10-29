// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Piston.Mcp.Tools;

/// <summary>How a file differs from the base.</summary>
internal enum DiffFileStatus
{
    Modified,
    Added,
    Deleted,

    /// <summary>Git reports the file as binary, so it has no hunks.</summary>
    Binary,
}

internal enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>One line of a hunk.</summary>
/// <param name="OldLine">The line's number in the base, for context and removed lines.</param>
/// <param name="NewLine">The line's number in the changed file, for context and added lines.</param>
/// <param name="Text">The line's text, without its line ending.</param>
internal sealed record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);

/// <summary>A hunk of a unified diff.</summary>
internal sealed record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLine> Lines);

/// <summary>One changed file of a unified diff.</summary>
internal sealed record DiffFile(DiffFileStatus Status, IReadOnlyList<DiffHunk> Hunks)
{
    /// <summary>The added lines' numbers in the changed file.</summary>
    public IEnumerable<int> AddedLines =>
        Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).Select(l => l.NewLine!.Value);
}

/// <summary>Parses <c>git diff</c> output (without renames) into files and hunks.</summary>
internal static partial class UnifiedDiff
{
    [GeneratedRegex(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    /// <summary>Parses the diff into repository-relative paths and their hunks.</summary>
    public static Dictionary<string, DiffFile> Parse(string diff)
    {
        var result = new Dictionary<string, DiffFile>(StringComparer.Ordinal);
        string? path = null;
        var status = DiffFileStatus.Modified;
        List<DiffHunk>? hunks = null;
        List<DiffLine>? lines = null;
        int oldStart = 0, oldCount = 0, newStart = 0, newCount = 0;
        int oldRemaining = 0, newRemaining = 0, oldLine = 0, newLine = 0;

        foreach (var line in diff.Split('\n'))
        {
            if (lines is not null && (oldRemaining > 0 || newRemaining > 0))
            {
                // Inside a hunk every line is content, even one that looks like a header ("--- x" removes "-- x").
                var content = TrimCr(line.Length > 0 ? line[1..] : line);
                switch (line.Length > 0 ? line[0] : ' ')
                {
                    case '+':
                        lines.Add(new DiffLine(DiffLineKind.Added, null, newLine++, content));
                        newRemaining--;
                        continue;
                    case '-':
                        lines.Add(new DiffLine(DiffLineKind.Removed, oldLine++, null, content));
                        oldRemaining--;
                        continue;
                    case '\\':
                        continue;
                    default:
                        lines.Add(new DiffLine(DiffLineKind.Context, oldLine++, newLine++, content));
                        oldRemaining--;
                        newRemaining--;
                        continue;
                }
            }

            if (line.StartsWith('\\'))
            {
                // "\ No newline at end of file" after a hunk's last line.
                continue;
            }

            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                path = HeaderPath(line["diff --git ".Length..]);
                status = DiffFileStatus.Modified;
                hunks = [];
                continue;
            }

            if (hunks is null)
            {
                continue;
            }

            if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                status = DiffFileStatus.Added;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                status = DiffFileStatus.Deleted;
            }
            else if (line.StartsWith("Binary files ", StringComparison.Ordinal) || line.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                status = DiffFileStatus.Binary;
            }
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                if (StripPrefix(Unquote(TrimCr(line[4..])), "a/") is { } source && status == DiffFileStatus.Deleted)
                {
                    path = source;
                }
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (StripPrefix(Unquote(TrimCr(line[4..])), "b/") is { } target)
                {
                    path = target;
                }
            }
            else if (HunkHeader().Match(line) is { Success: true } header)
            {
                FlushHunk();
                oldStart = Number(header.Groups[1]);
                oldCount = header.Groups[2].Success ? Number(header.Groups[2]) : 1;
                newStart = Number(header.Groups[3]);
                newCount = header.Groups[4].Success ? Number(header.Groups[4]) : 1;
                (oldRemaining, newRemaining) = (oldCount, newCount);
                // An empty side's start is the line before the hunk, so its first line is one past it.
                oldLine = oldCount == 0 ? oldStart + 1 : oldStart;
                newLine = newCount == 0 ? newStart + 1 : newStart;
                lines = [];
            }
        }

        Flush();
        return result;

        void FlushHunk()
        {
            if (lines is not null)
            {
                hunks!.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, lines));
                lines = null;
            }
        }

        void Flush()
        {
            if (hunks is null)
            {
                return;
            }

            FlushHunk();
            if (path is not null)
            {
                result[path] = new DiffFile(status, status == DiffFileStatus.Binary ? [] : hunks);
            }

            hunks = null;
            path = null;
        }

        static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);
    }

    /// <summary>A file that is new in full, e.g. an untracked file, as one hunk of added lines.</summary>
    public static DiffFile Added(IReadOnlyList<string> lines) => new(
        DiffFileStatus.Added,
        lines.Count == 0
            ? []
            : [new DiffHunk(0, 0, 1, lines.Count, [.. lines.Select((text, i) => new DiffLine(DiffLineKind.Added, null, i + 1, text))])]);

    private static string TrimCr(string value) => value.EndsWith('\r') ? value[..^1] : value;

    private static string? StripPrefix(string path, string prefix) =>
        path == "/dev/null" ? null : path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;

    /// <summary>
    /// The path of a <c>diff --git a/P b/P</c> header. Without renames both sides name the same path, which
    /// <c>+++</c>/<c>---</c> lines refine; binary files and mode-only changes have only this header.
    /// </summary>
    private static string? HeaderPath(string header)
    {
        header = TrimCr(header);
        if (header.StartsWith('"'))
        {
            var end = ClosingQuote(header);
            return end < 0 ? null : StripPrefix(Unquote(header[..(end + 1)]), "a/");
        }

        if ((header.Length - 5) % 2 != 0 || header.Length < 7)
        {
            return null;
        }

        var side = header[..((header.Length - 1) / 2)];
        return StripPrefix(side, "a/");
    }

    private static int ClosingQuote(string value)
    {
        for (var i = 1; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == '"')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Removes git's C-style quoting from a path, when present.</summary>
    internal static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        // Octal escapes are UTF-8 bytes, so unescape bytes rather than characters.
        var source = Encoding.UTF8.GetBytes(value[1..^1]);
        var bytes = new List<byte>(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var b = source[i];
            if (b != '\\' || i + 1 >= source.Length)
            {
                bytes.Add(b);
                continue;
            }

            var next = source[++i];
            if (next is >= (byte)'0' and <= (byte)'3' && i + 2 < source.Length
                && source[i + 1] is >= (byte)'0' and <= (byte)'7' && source[i + 2] is >= (byte)'0' and <= (byte)'7')
            {
                bytes.Add((byte)(((next - '0') << 6) | ((source[i + 1] - '0') << 3) | (source[i + 2] - '0')));
                i += 2;
                continue;
            }

            bytes.Add(next switch
            {
                (byte)'n' => (byte)'\n',
                (byte)'t' => (byte)'\t',
                (byte)'r' => (byte)'\r',
                (byte)'a' => 7,
                (byte)'b' => 8,
                (byte)'f' => 12,
                (byte)'v' => 11,
                _ => next,
            });
        }

        return Encoding.UTF8.GetString([.. bytes]);
    }
}
