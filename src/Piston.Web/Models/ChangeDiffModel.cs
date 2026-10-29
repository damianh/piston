// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>What the Coverage tab shows: every measured file, or the diff of the changes.</summary>
public enum CoverageMode
{
    Files,
    Changes,
}

/// <summary>An added line of a changed file.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Line">1-based line number in the changed file.</param>
public sealed record ChangeLineRef(string FilePath, int Line);

/// <summary>How a changed line's verdict is shown.</summary>
/// <param name="Label">Short name, e.g. "uncovered".</param>
/// <param name="Glyph">A mark that tells verdicts apart without colour.</param>
/// <param name="Description">What the verdict means.</param>
/// <param name="RowClass">Tint for the line.</param>
/// <param name="GutterClass">Colour of the gutter bar.</param>
/// <param name="TextClass">Colour for the label.</param>
public sealed record VerdictStyle(
    string Label, string Glyph, string Description, string RowClass, string GutterClass, string TextClass);

/// <summary>The tests recorded as executing a changed line.</summary>
/// <param name="Credited">Tests measured on the file as it is now: they ran the line, and their outcome counts.</param>
/// <param name="Outdated">Tests measured on an older version of the file, whose ranges include the line number but may not be the line.</param>
public sealed record LineTests(IReadOnlyList<CoveringTestDto> Credited, IReadOnlyList<CoveringTestDto> Outdated);

/// <summary>Pure helpers behind the Coverage tab's Changes view.</summary>
public static class ChangeDiffModel
{
    public const string Working = "working";
    public const string Branch = "branch";
    public const string Range = "range";

    public static readonly string[] Bases = [Working, Branch, Range];

    /// <summary>Most characters a base ref may have.</summary>
    public const int MaxBaseRefLength = 200;

    public static readonly string[] Verdicts =
        ["coveredByPassing", "coveredOnlyByFailing", "uncovered", "nonExecutable", "unknown"];

    /// <summary>Lines no passing test proves: uncovered, run only by failing tests, or with unknown coverage.</summary>
    public static bool IsUnproven(DiffLineDto line) =>
        line.Kind == "added" && line.Verdict is "uncovered" or "coveredOnlyByFailing" or "unknown";

    /// <summary>The unproven added lines, in file and line order.</summary>
    public static IReadOnlyList<ChangeLineRef> UnprovenLines(IEnumerable<ChangeDiffFileDto> files) =>
        [.. files.SelectMany(f => f.Hunks.SelectMany(h => h.Lines)
            .Where(IsUnproven)
            .Select(l => new ChangeLineRef(f.FilePath, l.NewLine!.Value)))];

    /// <summary>Files with at least one unproven line, or whose lines are unproven but not listed.</summary>
    public static bool HasUnproven(ChangeDiffFileDto file) =>
        file.Totals.Uncovered + file.Totals.CoveredOnlyByFailing + file.Totals.Unknown > 0;

    /// <summary>
    /// The hunks to show: every hunk, or with <paramref name="onlyUnproven"/> only those with an unproven line.
    /// </summary>
    public static IReadOnlyList<DiffHunkDto> VisibleHunks(ChangeDiffFileDto file, bool onlyUnproven) =>
        onlyUnproven ? [.. file.Hunks.Where(h => h.Lines.Any(IsUnproven))] : file.Hunks;

    /// <summary>
    /// The unproven line after (or with <paramref name="forward"/> false, before) <paramref name="current"/>,
    /// wrapping around. With no current line, or one that is no longer listed, the first line after its position
    /// (in <paramref name="fileOrder"/>) is next. Null when there are none.
    /// </summary>
    public static ChangeLineRef? Step(
        IReadOnlyList<ChangeLineRef> lines, ChangeLineRef? current, bool forward, IReadOnlyList<string> fileOrder)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        if (current is null)
        {
            return forward ? lines[0] : lines[^1];
        }

        var index = IndexOf(lines, current);
        if (index >= 0)
        {
            return lines[(index + (forward ? 1 : -1) + lines.Count) % lines.Count];
        }

        var position = Position(current);
        if (forward)
        {
            return lines.FirstOrDefault(l => Compare(Position(l), position) > 0) ?? lines[0];
        }

        return lines.LastOrDefault(l => Compare(Position(l), position) < 0) ?? lines[^1];

        (int File, int Line) Position(ChangeLineRef line)
        {
            var file = -1;
            for (var i = 0; i < fileOrder.Count; i++)
            {
                if (fileOrder[i] == line.FilePath)
                {
                    file = i;
                    break;
                }
            }

            return (file, line.Line);
        }

        static int Compare((int File, int Line) a, (int File, int Line) b) =>
            a.File != b.File ? a.File.CompareTo(b.File) : a.Line.CompareTo(b.Line);
    }

    private static int IndexOf(IReadOnlyList<ChangeLineRef> lines, ChangeLineRef line)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i] == line)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits the tests recorded as executing <paramref name="line"/>: credited tests, measured on the file as it is
    /// now, then outdated ones. Each list is ordered by how precisely it was measured, then by name. Outdated ranges
    /// were measured on other content, so for an <c>outdatedEvidence</c> <paramref name="reason"/> every outdated
    /// test with a pass or fail is listed, as the verdict weighs them wherever their ranges fall.
    /// </summary>
    public static LineTests TestsForLine(IEnumerable<CoveringTestDto> tests, int line, string? reason = null)
    {
        var ordered = tests
            .OrderBy(t => GranularityRank(t.Granularity))
            .ThenBy(t => t.TestFqn, StringComparer.Ordinal)
            .ToList();
        var outdated = reason == "outdatedEvidence"
            ? ordered.Where(t => !t.IsCurrent && t.Outcome is "passed" or "failed")
            : ordered.Where(t => !t.IsCurrent && Contains(t, line));
        return new([.. ordered.Where(t => t.IsCurrent && Contains(t, line))], [.. outdated]);

        static bool Contains(CoveringTestDto test, int line) => test.ExecutedLines.Any(r => r.Start <= line && line <= r.End);
    }

    private static readonly string[] Granularities = ["test", "class", "run"];

    private static int GranularityRank(string granularity) =>
        Array.IndexOf(Granularities, granularity) is var i and >= 0 ? i : Granularities.Length;

    public static VerdictStyle Style(string? verdict) => verdict switch
    {
        "coveredByPassing" => new("covered", "✓", "Run, and every current test credited with running it passed.",
            "bg-green-400/10", "bg-green-400", "text-green-400"),
        "coveredOnlyByFailing" => new("failing only", "✗", "Run, and every current test credited with running it failed.",
            "bg-coral/15", "bg-coral", "text-coral"),
        "uncovered" => new("uncovered", "○", "Executable, and no test that ran executed it.",
            "bg-red-400/15", "bg-red-400", "text-red-400"),
        "nonExecutable" => new("not code", "·", "Not executable: a blank line, comment, declaration or brace.",
            "", "bg-gray-700", "text-gray-500"),
        "unknown" => new("unknown", "?", "The evidence cannot prove this line either way.",
            "bg-yellow-400/10", "bg-yellow-400", "text-yellow-400"),
        _ => new("", "", "", "", "", "text-gray-500"),
    };

    /// <summary>Why a line's verdict is unknown, in words.</summary>
    public static string? ReasonText(string? reason) => reason switch
    {
        "stale" => "The line's code changed after it was measured, so its coverage does not describe it.",
        "notMeasured" => "No coverage run measured the file, e.g. it is not compiled into a tested assembly.",
        "noTestEvidence" => "The line ran, but no current evidence says which tests ran it.",
        "mixedEvidence" => "The tests credited with the line include both passing and failing tests.",
        "testsNotRun" => "A test credited with the line has no result, or every credited test was skipped.",
        "outdatedEvidence" => "Tests measured on an older version of the file may also run the line; their outcomes could change the verdict.",
        "resultsUpdating" => "A run was publishing results while the verdicts were read. Refresh when it finishes.",
        null => null,
        _ => reason,
    };

    /// <summary>The line's tooltip: its verdict, why, and its hits and branches.</summary>
    public static string? LineTitle(DiffLineDto line)
    {
        if (line.Verdict is null)
        {
            return null;
        }

        var style = Style(line.Verdict);
        var parts = new List<string> { $"{style.Glyph} {style.Label}: {style.Description}" };
        if (ReasonText(line.Reason) is { } reason)
        {
            parts.Add(reason);
        }

        if (line.HitCount is { } hits)
        {
            parts.Add(line.BranchesTotal is { } total
                ? $"{hits} hit(s) · {line.BranchesCovered}/{total} branches"
                : $"{hits} hit(s)");
        }

        return string.Join("\n", parts);
    }

    public static string OutcomeLabel(string? outcome) => outcome switch
    {
        "passed" => "passed",
        "failed" => "failed",
        "skipped" => "skipped",
        "noResult" => "no result",
        _ => "",
    };

    public static string OutcomeClass(string? outcome) => outcome switch
    {
        "passed" => "text-green-400",
        "failed" => "text-red-400",
        _ => "text-gray-500",
    };

    /// <summary>
    /// Highlights a C# hunk's lines, aligned with <see cref="DiffHunkDto.Lines"/>. The new side (context and added
    /// lines) and the old side (context and removed lines) are each highlighted as one sequence, so comments and
    /// strings that span lines are coloured as in the file.
    /// </summary>
    public static List<List<SourceToken>> Highlight(DiffHunkDto hunk)
    {
        // Context lines are on both sides; removed lines take the old side's tokens, the rest the new side's.
        var newSide = Enumerable.Range(0, hunk.Lines.Count).Where(i => hunk.Lines[i].Kind != "removed").ToList();
        var oldSide = Enumerable.Range(0, hunk.Lines.Count).Where(i => hunk.Lines[i].Kind != "added").ToList();
        var newTokens = CSharpHighlighter.Highlight([.. newSide.Select(i => hunk.Lines[i].Text)]);
        var oldTokens = CSharpHighlighter.Highlight([.. oldSide.Select(i => hunk.Lines[i].Text)]);
        var result = new List<SourceToken>[hunk.Lines.Count];
        for (var i = 0; i < newSide.Count; i++)
        {
            result[newSide[i]] = newTokens[i];
        }

        for (var i = 0; i < oldSide.Count; i++)
        {
            if (hunk.Lines[oldSide[i]].Kind == "removed")
            {
                result[oldSide[i]] = oldTokens[i];
            }
        }

        return [.. result];
    }

    /// <summary>The base and ref to ask for: a ref is sent only for branch and range.</summary>
    public static (string Base, string? BaseRef) Request(string? @base, string? baseRef)
    {
        var chosen = Bases.Contains(@base) ? @base! : Working;
        var trimmed = baseRef?.Trim();
        return (chosen, chosen == Working || string.IsNullOrEmpty(trimmed) ? null : trimmed);
    }
}
