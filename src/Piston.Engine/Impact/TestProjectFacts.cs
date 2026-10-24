// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>What a test project's evaluated MSBuild properties and package references say about how it runs tests.</summary>
/// <param name="OutputType">The <c>OutputType</c> property (<c>Exe</c>, <c>Library</c>, …), or empty.</param>
/// <param name="IsMtp">Whether the project runs on Microsoft.Testing.Platform.</param>
/// <param name="PackageReferences">The project's package references (names only).</param>
public sealed record TestProjectFacts(string OutputType, bool IsMtp, IReadOnlySet<string> PackageReferences)
{
    public bool References(string package) => PackageReferences.Contains(package);
}
