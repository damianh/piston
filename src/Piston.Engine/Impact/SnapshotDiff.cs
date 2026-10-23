// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// Returns the old-source line range containing changed C# tokens, ignoring ordinary comments and whitespace.
    /// Directives, documentation comments and disabled preprocessor text remain significant.
    /// </summary>
    public static LineSpan? ComputeCodeRange(string oldContent, string newContent) =>
        ComputeCodeRange(oldContent, CodeItems(oldContent), CodeItems(newContent));

    internal static LineSpan? ComputeCodeRange(
        string oldContent,
        IReadOnlyList<CodeItem> oldItems,
        IReadOnlyList<CodeItem> newItems)
    {
        var prefix = 0;
        var max = Math.Min(oldItems.Count, newItems.Count);
        while (prefix < max && oldItems[prefix].EquivalentTo(newItems[prefix]))
        {
            prefix++;
        }

        if (prefix == oldItems.Count && prefix == newItems.Count)
        {
            return null;
        }

        var suffix = 0;
        while (suffix < max - prefix
            && oldItems[oldItems.Count - 1 - suffix].EquivalentTo(newItems[newItems.Count - 1 - suffix]))
        {
            suffix++;
        }

        var oldEnd = oldItems.Count - suffix;
        var sourceText = Microsoft.CodeAnalysis.Text.SourceText.From(oldContent);
        if (prefix < oldEnd)
        {
            var start = oldItems[prefix].Start;
            var end = oldItems[oldEnd - 1].End;
            var firstLine = sourceText.Lines.GetLineFromPosition(start).LineNumber + 1;
            var lastLine = sourceText.Lines.GetLineFromPosition(Math.Max(start, end - 1)).LineNumber + 1;
            return new LineSpan(firstLine, lastLine);
        }

        var position = prefix < oldItems.Count ? oldItems[prefix].Start : oldContent.Length;
        var line = sourceText.Lines.GetLineFromPosition(position).LineNumber + 1;
        return new LineSpan(Math.Max(1, line - 1), line);
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

    internal static IReadOnlyList<CodeItem> CodeItems(string content)
    {
        var root = CSharpSyntaxTree.ParseText(content).GetRoot();
        var items = root.DescendantTokens()
            .Where(token => !token.IsMissing && !token.IsKind(SyntaxKind.EndOfFileToken))
            .Select(token => new CodeItem(token.RawKind, IsTrivia: false, token.Text, token.SpanStart, token.Span.End))
            .ToList();

        items.AddRange(root.DescendantTrivia(descendIntoTrivia: true)
            .Where(trivia => trivia.IsDirective
                || trivia.IsKind(SyntaxKind.DisabledTextTrivia)
                || trivia.GetStructure() is DocumentationCommentTriviaSyntax)
            .Select(trivia => new CodeItem(trivia.RawKind, IsTrivia: true, trivia.ToFullString(), trivia.SpanStart, trivia.Span.End)));

        return [.. items.OrderBy(item => item.Start).ThenBy(item => item.IsTrivia ? 0 : 1)];
    }

    internal readonly record struct CodeItem(int RawKind, bool IsTrivia, string Text, int Start, int End)
    {
        public bool EquivalentTo(CodeItem other) =>
            RawKind == other.RawKind && IsTrivia == other.IsTrivia && string.Equals(Text, other.Text, StringComparison.Ordinal);
    }
}
