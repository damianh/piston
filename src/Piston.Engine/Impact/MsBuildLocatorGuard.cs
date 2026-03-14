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
    private static readonly object Lock = new();
    private static bool _registered;

    /// <summary>
    /// Call this before constructing any type that references <c>Microsoft.Build.*</c>.
    /// Must be invoked from a call site that does NOT directly reference MSBuild types,
    /// so that the assembly resolver hook is in place before the JIT loads MSBuild.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    internal static void EnsureRegistered()
    {
        if (_registered) return;
        lock (Lock)
        {
            if (_registered) return;
            MSBuildLocator.RegisterDefaults();
            _registered = true;
        }
    }
}
