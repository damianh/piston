// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace Piston.Web.Models;

/// <summary>A line of a stack trace, with the source location it names, if any.</summary>
/// <param name="Text">The line as written.</param>
/// <param name="FilePath">The source file, when the frame has one.</param>
/// <param name="Line">The 1-based line number in <paramref name="FilePath"/>.</param>
public sealed record StackFrameLine(string Text, string? FilePath, int? Line)
{
    public bool HasSource => FilePath is not null && Line is not null;
}

/// <summary>Splits .NET stack traces into frames and finds the source location each names.</summary>
public static partial class StackTraceParser
{
    // VSTest writes "   at Ns.Type.Method(Args) in /path/File.cs:line 42"; MTP writes "...File.cs:42".
    [GeneratedRegex(@"^\s*at .+? in (?<file>.+):(?:line )?(?<line>\d+)\s*$")]
    private static partial Regex FrameWithSource();

    public static IReadOnlyList<StackFrameLine> Parse(string? stackTrace)
    {
        if (string.IsNullOrEmpty(stackTrace))
        {
            return [];
        }

        var frames = new List<StackFrameLine>();
        foreach (var raw in stackTrace.Split('\n'))
        {
            var text = raw.TrimEnd('\r');
            if (text.Length == 0)
            {
                continue;
            }

            var match = FrameWithSource().Match(text);
            frames.Add(match.Success && int.TryParse(match.Groups["line"].ValueSpan, out var line) && line > 0
                ? new StackFrameLine(text, match.Groups["file"].Value, line)
                : new StackFrameLine(text, null, null));
        }

        return frames;
    }

    /// <summary>
    /// The frame to show source for: the first one in a file under <paramref name="solutionDirectory"/>, so the
    /// failing line of the user's code rather than of a framework; null when no frame names such a file.
    /// </summary>
    public static StackFrameLine? FirstInSolution(IReadOnlyList<StackFrameLine> frames, string? solutionDirectory) =>
        frames.FirstOrDefault(f => f.HasSource && IsInSolution(f.FilePath!, solutionDirectory));

    public static bool IsInSolution(string filePath, string? solutionDirectory)
    {
        if (string.IsNullOrEmpty(solutionDirectory))
        {
            return false;
        }

        var root = solutionDirectory.TrimEnd('/', '\\');
        // These are the daemon's paths, not the browser host's paths.
        var windowsPath = (root.Length >= 2 && char.IsAsciiLetter(root[0]) && root[1] == ':')
            || root.StartsWith(@"\\", StringComparison.Ordinal)
            || root.StartsWith("//", StringComparison.Ordinal);
        if (windowsPath)
        {
            root = root.Replace('\\', '/');
            filePath = filePath.Replace('\\', '/');
        }

        return filePath.Length > root.Length
            && filePath.StartsWith(root, windowsPath ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && filePath[root.Length] is '/' or '\\';
    }
}
