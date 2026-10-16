// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;

namespace Piston.Engine.Services;

/// <summary>
/// Parsed VSTest test case filter (<c>dotnet test --filter</c> syntax), evaluated locally so it can be
/// applied to tests discovered over the Microsoft.Testing.Platform server protocol. Supports
/// <c>|</c>, <c>&amp;</c>, parentheses, the operators <c>=</c>, <c>!=</c>, <c>~</c> and <c>!~</c>, bare values
/// (meaning <c>FullyQualifiedName~value</c>) and <c>\</c> escapes. Comparisons are case-insensitive.
/// </summary>
internal abstract class TestFilterExpression
{
    /// <summary>
    /// Evaluates the filter for a test. <paramref name="valuesOf"/> returns the candidate values of a
    /// property (for example <c>FullyQualifiedName</c>); a positive condition matches when any candidate
    /// matches, a negated one when none does.
    /// </summary>
    public abstract bool Matches(Func<string, IReadOnlyList<string>> valuesOf);

    /// <summary>Parses <paramref name="filter"/>; returns <see langword="false"/> for unsupported syntax.</summary>
    public static bool TryParse(string filter, out TestFilterExpression? expression)
    {
        var parser = new Parser(filter);
        try
        {
            expression = parser.ParseOr();
            parser.SkipWhitespace();
            if (!parser.AtEnd)
            {
                expression = null;
                return false;
            }

            return true;
        }
        catch (FormatException)
        {
            expression = null;
            return false;
        }
    }

    private sealed class Or(IReadOnlyList<TestFilterExpression> operands) : TestFilterExpression
    {
        public override bool Matches(Func<string, IReadOnlyList<string>> valuesOf) =>
            operands.Any(o => o.Matches(valuesOf));
    }

    private sealed class And(IReadOnlyList<TestFilterExpression> operands) : TestFilterExpression
    {
        public override bool Matches(Func<string, IReadOnlyList<string>> valuesOf) =>
            operands.All(o => o.Matches(valuesOf));
    }

    private sealed class Condition(string property, bool contains, bool negated, string value) : TestFilterExpression
    {
        public override bool Matches(Func<string, IReadOnlyList<string>> valuesOf)
        {
            var any = valuesOf(property).Any(candidate => contains
                ? candidate.Contains(value, StringComparison.OrdinalIgnoreCase)
                : candidate.Equals(value, StringComparison.OrdinalIgnoreCase));
            return any != negated;
        }
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public void SkipWhitespace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
            {
                _pos++;
            }
        }

        public TestFilterExpression ParseOr()
        {
            var operands = new List<TestFilterExpression> { ParseAnd() };
            while (TryConsume('|'))
            {
                operands.Add(ParseAnd());
            }

            return operands.Count == 1 ? operands[0] : new Or(operands);
        }

        private TestFilterExpression ParseAnd()
        {
            var operands = new List<TestFilterExpression> { ParseFactor() };
            while (TryConsume('&'))
            {
                operands.Add(ParseFactor());
            }

            return operands.Count == 1 ? operands[0] : new And(operands);
        }

        private TestFilterExpression ParseFactor()
        {
            if (TryConsume('('))
            {
                var inner = ParseOr();
                if (!TryConsume(')'))
                {
                    throw new FormatException("Unbalanced parentheses.");
                }

                return inner;
            }

            return ParseCondition();
        }

        private bool TryConsume(char c)
        {
            SkipWhitespace();
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }

            return false;
        }

        // Reads up to the next unescaped '|', '&' or unmatched ')', keeping parentheses inside the value
        // balanced so unescaped test names such as "Ns.C.M(n: 1)" parse. Escaped characters are flagged
        // so they are never taken as operators.
        private TestFilterExpression ParseCondition()
        {
            SkipWhitespace();
            var chars = new List<(char Char, bool Escaped)>();
            var depth = 0;
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '\\')
                {
                    if (_pos + 1 >= text.Length)
                    {
                        throw new FormatException("Dangling escape.");
                    }

                    chars.Add((text[_pos + 1], true));
                    _pos += 2;
                    continue;
                }

                if (depth == 0 && c is '|' or '&' or ')')
                {
                    break;
                }

                depth += c switch { '(' => 1, ')' => -1, _ => 0 };
                chars.Add((c, false));
                _pos++;
            }

            for (var i = 0; i < chars.Count; i++)
            {
                if (chars[i].Escaped || chars[i].Char is not ('=' or '~' or '!'))
                {
                    continue;
                }

                var negated = chars[i].Char == '!';
                var opIndex = negated ? i + 1 : i;
                if (negated && (opIndex >= chars.Count || chars[opIndex].Escaped || chars[opIndex].Char is not ('=' or '~')))
                {
                    continue;
                }

                var property = Text(chars.Take(i)).Trim();
                if (property.Length == 0 || !property.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_'))
                {
                    break;
                }

                var value = Text(chars.Skip(opIndex + 1)).Trim();
                if (value.Length == 0)
                {
                    throw new FormatException("Missing value.");
                }

                return new Condition(property, chars[opIndex].Char == '~', negated, value);
            }

            var bare = Text(chars).Trim();
            return bare.Length == 0
                ? throw new FormatException("Empty condition.")
                : new Condition("FullyQualifiedName", contains: true, negated: false, bare);
        }

        private static string Text(IEnumerable<(char Char, bool Escaped)> chars)
        {
            var sb = new StringBuilder();
            foreach (var (c, _) in chars)
            {
                _ = sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
