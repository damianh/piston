// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

public enum SourceTokenKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
}

public sealed record SourceToken(string Text, SourceTokenKind Kind);

/// <summary>
/// A light, line-by-line C# highlighter for showing source in the dashboard: keywords, strings, comments and
/// numbers. Block comments and raw string literals carry over to the following lines; other multi-line constructs
/// (such as verbatim strings) are not tracked.
/// </summary>
public static class CSharpHighlighter
{
    public static string TokenClass(SourceTokenKind kind) => kind switch
    {
        SourceTokenKind.Keyword => "text-red-300",
        SourceTokenKind.String => "text-sky-400",
        SourceTokenKind.Comment => "text-gray-500 italic",
        SourceTokenKind.Number => "text-blue-400",
        _ => "",
    };

    private static readonly HashSet<string> Keywords =
    [
        "abstract", "as", "async", "await", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
        "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "get", "goto", "if",
        "implicit", "in", "init", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null",
        "object", "operator", "out", "override", "params", "partial", "private", "protected", "public", "readonly",
        "record", "ref", "required", "return", "sbyte", "sealed", "set", "short", "sizeof", "stackalloc", "static",
        "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked",
        "unsafe", "ushort", "using", "var", "virtual", "void", "volatile", "when", "where", "while", "with", "yield",
    ];

    /// <summary>Tokenizes <paramref name="lines"/>, one token list per line.</summary>
    public static List<List<SourceToken>> Highlight(IReadOnlyList<string> lines)
    {
        var result = new List<List<SourceToken>>(lines.Count);
        // The delimiter closing a block comment or raw string left open by an earlier line.
        (string Closer, SourceTokenKind Kind)? open = null;
        foreach (var line in lines)
        {
            result.Add(HighlightLine(line, ref open));
        }

        return result;
    }

    private static List<SourceToken> HighlightLine(string line, ref (string Closer, SourceTokenKind Kind)? open)
    {
        var tokens = new List<SourceToken>();
        var plainStart = 0;
        var i = 0;

        void Emit(int start, int end, SourceTokenKind kind)
        {
            if (start > plainStart)
            {
                tokens.Add(new SourceToken(line[plainStart..start], SourceTokenKind.Plain));
            }

            tokens.Add(new SourceToken(line[start..end], kind));
            plainStart = end;
        }

        if (open is { } carried)
        {
            var close = line.IndexOf(carried.Closer, StringComparison.Ordinal);
            var end = close < 0 ? line.Length : close + carried.Closer.Length;
            if (end > 0)
            {
                Emit(0, end, carried.Kind);
            }

            open = close < 0 ? carried : null;
            i = end;
        }

        while (i < line.Length)
        {
            var c = line[i];
            if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                Emit(i, line.Length, SourceTokenKind.Comment);
                i = line.Length;
            }
            else if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
            {
                var close = line.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var end = close < 0 ? line.Length : close + 2;
                Emit(i, end, SourceTokenKind.Comment);
                open = close < 0 ? ("*/", SourceTokenKind.Comment) : null;
                i = end;
            }
            else if (c is '"' or '\'' || (c is '@' or '$' && i + 1 < line.Length && line[i + 1] is '"' or '@' or '$'))
            {
                var end = StringEnd(line, i, out var unclosedRaw);
                Emit(i, end, SourceTokenKind.String);
                open = unclosedRaw is null ? null : (unclosedRaw, SourceTokenKind.String);
                i = end;
            }
            else if (char.IsDigit(c) && (i == 0 || !IsIdentifierChar(line[i - 1])))
            {
                var end = i + 1;
                while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '.' or '_'))
                {
                    end++;
                }

                Emit(i, end, SourceTokenKind.Number);
                i = end;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var end = i + 1;
                while (end < line.Length && IsIdentifierChar(line[end]))
                {
                    end++;
                }

                if ((i == 0 || line[i - 1] != '@') && Keywords.Contains(line[i..end]))
                {
                    Emit(i, end, SourceTokenKind.Keyword);
                }

                i = end;
            }
            else
            {
                i++;
            }
        }

        if (plainStart < line.Length)
        {
            tokens.Add(new SourceToken(line[plainStart..], SourceTokenKind.Plain));
        }

        return tokens;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // The end of the string or char literal starting at start: its prefixes (@, $), then up to the matching quote.
    // unclosedRaw is the closing quote run of a raw string literal that continues on the next line.
    private static int StringEnd(string line, int start, out string? unclosedRaw)
    {
        unclosedRaw = null;
        var i = start;
        var verbatim = false;
        while (i < line.Length && line[i] is '@' or '$')
        {
            verbatim |= line[i] == '@';
            i++;
        }

        if (i >= line.Length || line[i] is not ('"' or '\''))
        {
            return i;
        }

        var quote = line[i];
        var quotes = 0;
        while (i + quotes < line.Length && line[i + quotes] == quote && quote == '"')
        {
            quotes++;
        }

        // A raw string literal: """ ... """
        if (quotes >= 3)
        {
            var closer = new string('"', quotes);
            var close = line.IndexOf(closer, i + quotes, StringComparison.Ordinal);
            if (close < 0)
            {
                unclosedRaw = closer;
                return line.Length;
            }

            return close + quotes;
        }

        i++;
        while (i < line.Length)
        {
            if (line[i] == '\\' && !verbatim)
            {
                i += 2;
            }
            else if (line[i] == quote)
            {
                if (verbatim && i + 1 < line.Length && line[i + 1] == quote)
                {
                    i += 2;
                }
                else
                {
                    return i + 1;
                }
            }
            else
            {
                i++;
            }
        }

        return line.Length;
    }
}
