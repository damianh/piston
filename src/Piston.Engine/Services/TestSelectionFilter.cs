// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;

namespace Piston.Engine.Services;

/// <summary>Builds the test filter for a <see cref="Models.TestSelection"/>.</summary>
internal static class TestSelectionFilter
{
    /// <summary>Most conditions in one filter, to keep the <c>dotnet test</c> command line short.</summary>
    internal const int MaxConditions = 100;

    /// <summary>
    /// A filter matching <paramref name="tests"/> by method name, or by class when there are more than
    /// <see cref="MaxConditions"/> methods; null (run everything) when there are none or too many classes.
    /// </summary>
    public static string? Build(IReadOnlyList<string>? tests)
    {
        if (tests is null)
        {
            return null;
        }

        var methods = tests
            .Select(MethodName)
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        if (methods.Count == 0)
        {
            return null;
        }

        if (methods.Count <= MaxConditions)
        {
            return string.Join("|", methods.Select(m => $"FullyQualifiedName={Escape(m)}"));
        }

        var classes = methods
            .Select(m => m.LastIndexOf('.') is var dot and > 0 ? m[..dot] : m)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return classes.Count <= MaxConditions
            ? string.Join("|", classes.Select(c => $"FullyQualifiedName~{Escape(c + ".")}"))
            : null;
    }

    // A parameterised test's name can carry its arguments, which may contain dots and filter syntax.
    internal static string MethodName(string fullyQualifiedName)
    {
        var paren = fullyQualifiedName.IndexOf('(', StringComparison.Ordinal);
        return (paren < 0 ? fullyQualifiedName : fullyQualifiedName[..paren]).Trim();
    }

    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch is '\\' or '(' or ')' or '&' or '|' or '=' or '!' or '~')
            {
                _ = builder.Append('\\');
            }

            _ = builder.Append(ch);
        }

        return builder.ToString();
    }
}
