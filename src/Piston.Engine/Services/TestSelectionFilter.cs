// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using Piston.Engine.Models;

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
    /// <param name="tests">
    /// Identities (<see cref="TestResult.FullyQualifiedName"/>) of the tests to run. An identity that matches no
    /// known result exactly is matched by name with any argument list removed.
    /// </param>
    /// <param name="known">
    /// Results for the selected tests' projects, which map an identity built from a custom display name to the
    /// method a filter must name.
    /// </param>
    /// <exception cref="ArgumentException">A test name contains a double quote.</exception>
    public static string? Build(IReadOnlyList<string>? tests, IEnumerable<TestResult>? known = null)
    {
        if (tests is null)
        {
            return null;
        }

        var withMethod = (known ?? []).Where(t => t.Method is not null).ToList();

        // Exact identities first: distinct display names such as "checkout (guest)" and "checkout (member)"
        // share a name once their argument lists are stripped, but each maps to its own method.
        var methodByIdentity = withMethod
            .GroupBy(t => t.FullyQualifiedName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => MethodName(g.First().Method!), StringComparer.Ordinal);
        var methodsByName = withMethod
            .ToLookup(t => MethodName(t.FullyQualifiedName), t => MethodName(t.Method!), StringComparer.Ordinal);

        var methods = tests
            .SelectMany(t => methodByIdentity.TryGetValue(t, out var method)
                ? [method]
                : methodsByName.Contains(MethodName(t)) ? methodsByName[MethodName(t)] : [MethodName(t)])
            .Where(m => m.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        // The filter is passed as one quoted command-line argument, which a quote would end.
        if (methods.FirstOrDefault(m => m.Contains('"', StringComparison.Ordinal)) is { } quoted)
        {
            throw new ArgumentException($"Test name '{quoted}' contains a double quote.", nameof(tests));
        }

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
