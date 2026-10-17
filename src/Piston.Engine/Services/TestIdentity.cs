// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>
/// The identity Piston gives a VSTest test case: its display name qualified with its class, as
/// <see cref="TrxResultParser"/> derives it from a TRX file. Live collector events carry the test
/// case's fully qualified name and display name, from which the same identity is derived, so live
/// and final results for a test line up even when the display name is unqualified.
/// </summary>
internal static class TestIdentity
{
    /// <summary>The identity of a test case from its VSTest fully qualified and display names.</summary>
    internal static string Of(string fullyQualifiedName, string displayName) =>
        string.IsNullOrEmpty(fullyQualifiedName)
            ? displayName
            : Qualify(ClassNameOf(fullyQualifiedName, displayName), displayName);

    /// <summary>
    /// Prefixes <paramref name="testName"/> with <paramref name="className"/> unless it is already
    /// qualified with it (xUnit display names are, e.g. <c>Ns.Class.Method</c>).
    /// </summary>
    internal static string Qualify(string className, string testName) =>
        string.IsNullOrEmpty(className) || testName.StartsWith(className + ".", StringComparison.Ordinal)
            ? testName
            : $"{className}.{testName}";

    /// <summary>
    /// The class name the TRX logger records for a test case (<c>TestMethod/@className</c>): the fully
    /// qualified name, less the display name when it ends with it, up to the last dot that is not inside
    /// an argument list, generic argument list or string literal.
    /// </summary>
    internal static string ClassNameOf(string fullyQualifiedName, string displayName)
    {
        var nameToCheck = !fullyQualifiedName.Equals(displayName, StringComparison.OrdinalIgnoreCase)
            && fullyQualifiedName.EndsWith(displayName, StringComparison.Ordinal)
                ? fullyQualifiedName[..^displayName.Length]
                : fullyQualifiedName;

        var dot = LastTopLevelIndexOf(nameToCheck, ".");
        if (dot >= 0)
        {
            return nameToCheck[..dot];
        }

        var scope = LastTopLevelIndexOf(nameToCheck, "::");
        return scope >= 0 ? nameToCheck[..scope].Replace("::", ".", StringComparison.Ordinal) : string.Empty;
    }

    /// <summary>
    /// The last index of <paramref name="separator"/> in <paramref name="name"/> outside brackets and quotes,
    /// so dots in arguments (<c>M(x: 1.5, s: "a.b")</c>) or generic arguments (<c>M&lt;System.Int32&gt;</c>)
    /// are not taken for the class/method separator.
    /// </summary>
    private static int LastTopLevelIndexOf(string name, string separator)
    {
        var last = -1;
        var depth = 0;
        char? quote = null;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (quote is not null)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == quote)
                {
                    quote = null;
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'' when depth > 0:
                    quote = c;
                    break;
                case '(' or '<' or '[':
                    depth++;
                    break;
                case ')' or '>' or ']' when depth > 0:
                    depth--;
                    break;
                default:
                    if (depth == 0 && string.CompareOrdinal(name, i, separator, 0, separator.Length) == 0)
                    {
                        last = i;
                    }

                    break;
            }
        }

        return last;
    }
}
