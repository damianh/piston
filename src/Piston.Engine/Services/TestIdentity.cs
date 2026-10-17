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
    /// qualified name, less the display name when it ends with it, up to the last dot.
    /// </summary>
    internal static string ClassNameOf(string fullyQualifiedName, string displayName)
    {
        var nameToCheck = !fullyQualifiedName.Equals(displayName, StringComparison.OrdinalIgnoreCase)
            && fullyQualifiedName.EndsWith(displayName, StringComparison.Ordinal)
                ? fullyQualifiedName[..^displayName.Length]
                : fullyQualifiedName;

        var dot = nameToCheck.LastIndexOf('.');
        if (dot >= 0)
        {
            return nameToCheck[..dot];
        }

        var scope = nameToCheck.LastIndexOf("::", StringComparison.Ordinal);
        return scope >= 0 ? nameToCheck[..scope].Replace("::", ".", StringComparison.Ordinal) : string.Empty;
    }
}
