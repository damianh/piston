// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace Piston.Engine.Impact;

/// <summary>Per-line branch outcomes observed in one coverage report.</summary>
/// <param name="Line">1-based source line.</param>
/// <param name="Covered">Branch outcomes taken.</param>
/// <param name="Total">Branch outcomes reported.</param>
internal readonly record struct BranchLine(int Line, int Covered, int Total)
{
    /// <summary>Compact storage form, e.g. <c>12:1/2,15:0/2</c>.</summary>
    public static string Format(IEnumerable<BranchLine> branches)
    {
        var builder = new StringBuilder();
        foreach (var branch in branches.OrderBy(b => b.Line))
        {
            if (builder.Length > 0)
            {
                _ = builder.Append(',');
            }

            _ = builder.Append(branch.Line.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(branch.Covered.ToString(CultureInfo.InvariantCulture))
                .Append('/')
                .Append(branch.Total.ToString(CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }

    /// <summary>Parses the output of <see cref="Format"/>; malformed parts are skipped.</summary>
    public static IReadOnlyList<BranchLine> Parse(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var branches = new List<BranchLine>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = part.IndexOf(':', StringComparison.Ordinal);
            var slash = colon < 0 ? -1 : part.IndexOf('/', colon + 1);
            if (colon <= 0 || slash <= colon + 1
                || !int.TryParse(part.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var line)
                || !int.TryParse(part.AsSpan(colon + 1, slash - colon - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var covered)
                || !int.TryParse(part.AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var total)
                || line <= 0 || covered < 0 || total <= 0 || covered > total)
            {
                continue;
            }

            branches.Add(new BranchLine(line, covered, total));
        }

        return Merge(branches);
    }

    /// <summary>Combines observations for the same line by retaining the largest reported counts.</summary>
    public static IReadOnlyList<BranchLine> Merge(IEnumerable<BranchLine> branches) =>
        [.. branches
            .Where(b => b.Line > 0 && b.Covered >= 0 && b.Total > 0)
            .GroupBy(b => b.Line)
            .Select(g =>
            {
                var total = g.Max(b => b.Total);
                return new BranchLine(g.Key, Math.Min(g.Max(b => b.Covered), total), total);
            })
            .OrderBy(b => b.Line)];

    /// <summary>Remaps evidence that lies outside the changed region; replaced-line evidence is discarded.</summary>
    public static IReadOnlyList<BranchLine> Remap(IEnumerable<BranchLine> branches, SnapshotDiff diff)
    {
        var delta = diff.NewCount - diff.OldCount;
        return Merge(branches
            .Where(branch => branch.Line <= diff.Prefix || branch.Line > diff.Prefix + diff.OldCount)
            .Select(branch => branch.Line > diff.Prefix + diff.OldCount
                ? branch with { Line = branch.Line + delta }
                : branch));
    }

    /// <summary>Maps branch lines through comment or whitespace edits while preserving identical C# tokens.</summary>
    public static IReadOnlyList<BranchLine> RemapEquivalent(
        IEnumerable<BranchLine> branches,
        string oldContent,
        string newContent)
    {
        var oldTokens = SnapshotDiff.CodeItems(oldContent).Where(item => !item.IsTrivia).ToArray();
        var newTokens = SnapshotDiff.CodeItems(newContent).Where(item => !item.IsTrivia).ToArray();
        if (oldTokens.Length != newTokens.Length
            || oldTokens.Where((token, index) => !token.EquivalentTo(newTokens[index])).Any())
        {
            return [];
        }

        var oldText = SourceText.From(oldContent);
        var newText = SourceText.From(newContent);
        return Merge(branches.Select(branch =>
        {
            if (branch.Line > oldText.Lines.Count)
            {
                return branch with { Line = 0 };
            }

            var oldLine = oldText.Lines[branch.Line - 1];
            var tokenIndex = Array.FindIndex(oldTokens, token => token.Start >= oldLine.Start && token.Start <= oldLine.End);
            return tokenIndex < 0
                ? branch with { Line = 0 }
                : branch with { Line = newText.Lines.GetLineFromPosition(newTokens[tokenIndex].Start).LineNumber + 1 };
        }));
    }
}
