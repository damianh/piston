// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>Maps test project paths to the suite names their results are reported under.</summary>
internal static class TestProjectNames
{
    internal static bool IsProjectFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    /// <summary>The project's file name without extension (e.g. <c>MyApp.Tests</c>).</summary>
    internal static string SuiteName(string path) => Path.GetFileNameWithoutExtension(path);
}
