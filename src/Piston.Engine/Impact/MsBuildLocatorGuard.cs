// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.Build.Locator;

namespace Piston.Engine.Impact;

/// <summary>
/// Ensures MSBuild is located exactly once before any MSBuild APIs are used.
/// This class must NOT import or reference any <c>Microsoft.Build.*</c> types other
/// than <see cref="MSBuildLocator"/> — the locator itself does not require MSBuild to
/// be loaded yet.
/// </summary>
internal static class MsBuildLocatorGuard
{
    // MSBuildLocator exports these so in-process MSBuild finds the registered SDK.
    // Child `dotnet` processes inherit them, and when the `dotnet` on PATH is a different
    // installation its MSBuild loads a foreign SDK and fails with "Build FAILED. 0 Error(s)".
    internal static readonly string[] LocatorEnvironmentVariables =
        ["MSBUILD_EXE_PATH", "MSBuildExtensionsPath", "MSBuildSDKsPath"];

    private static readonly object Lock = new();
    private static bool _registered;
    private static Dictionary<string, string?>? _originalEnvironment;

    internal static IReadOnlyDictionary<string, string?>? OriginalEnvironment => _originalEnvironment;

    /// <summary>
    /// Call this before constructing any type that references <c>Microsoft.Build.*</c>.
    /// Must be invoked from a call site that does NOT directly reference MSBuild types,
    /// so that the assembly resolver hook is in place before the JIT loads MSBuild.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    internal static void EnsureRegistered()
    {
        if (_registered)
        {
            return;
        }

        lock (Lock)
        {
            if (_registered)
            {
                return;
            }

            _originalEnvironment = LocatorEnvironmentVariables.ToDictionary(
                name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
            _ = MSBuildLocator.RegisterDefaults();
            _registered = true;
        }
    }

    /// <summary>
    /// Restores the MSBuild location variables that existed before registration so a child
    /// <c>dotnet</c> process resolves its own SDK instead of the one this process loaded.
    /// </summary>
    internal static void RestoreOriginalEnvironment(ProcessStartInfo psi)
    {
        var original = _originalEnvironment;
        if (original is null)
        {
            return;
        }

        foreach (var (name, value) in original)
        {
            if (value is null)
            {
                _ = psi.Environment.Remove(name);
            }
            else
            {
                psi.Environment[name] = value;
            }
        }
    }
}
