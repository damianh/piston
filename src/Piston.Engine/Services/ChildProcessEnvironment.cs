// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Impact;

namespace Piston.Engine.Services;

/// <summary>
/// The environment of the <c>dotnet</c> processes Piston starts (builds, test hosts, test listing): the
/// daemon's own environment without variables that describe the process Piston was started from rather
/// than the user's machine. Piston run from <c>dotnet watch</c>, an IDE launch profile or an Aspire AppHost
/// would otherwise hand its listen address, hot reload agent or AppHost back-channel to every test host:
/// a test starting Kestrel from configuration binds the wrong port, and a nested AppHost talks to the
/// outer <c>aspire</c> CLI and hangs.
/// </summary>
public static class ChildProcessEnvironment
{
    /// <summary>Names, or <c>PREFIX*</c> patterns, separated by <c>;</c> or <c>,</c>, of variables child processes keep.</summary>
    public const string PassThroughVariable = "PISTON_TEST_ENV_PASSTHROUGH";

    private const string StartupHooks = "DOTNET_STARTUP_HOOKS";

    internal const string DiffEngineDisabled = "DiffEngine_Disabled";

    private static readonly string[] RemovedPrefixes =
    [
        "ASPIRE_",
        "DOTNET_DASHBOARD_",
        "DOTNET_RESOURCE_SERVICE_",
        "DCP_",
        "DEBUG_SESSION_",
        "DOTNET_WATCH",
        "DOTNET_HOTRELOAD_",
    ];

    private static readonly HashSet<string> RemovedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASPNETCORE_URLS", "ASPNETCORE_HTTP_PORTS", "ASPNETCORE_HTTPS_PORTS", "ASPNETCORE_HTTPS_PORT",
        "DOTNET_URLS", "DOTNET_HTTP_PORTS", "DOTNET_HTTPS_PORTS",
        "DOTNET_LAUNCH_PROFILE",
        "DOTNET_MODIFIABLE_ASSEMBLIES",
    };

    // Machine or user settings that `dotnet test` in a terminal would see too.
    private static readonly HashSet<string> KeptNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ASPIRE_CONTAINER_RUNTIME", "ASPIRE_DEPENDENCY_CHECK_TIMEOUT", "ASPIRE_SERVICE_STARTUP_WATCH_TIMEOUT",
        "ASPIRE_ENABLE_CONTAINER_TUNNEL", "ASPIRE_LOCALE_OVERRIDE", "ASPIRE_HOME", "ASPIRE_VERSION_CHECK_DISABLED",
        "ASPIRE_CLI_TELEMETRY_OPTOUT",
    };

    private static IReadOnlyList<string>? _passThrough;

    /// <summary>The pass-through patterns of this process, from <see cref="PassThroughVariable"/>.</summary>
    internal static IReadOnlyList<string> PassThrough
    {
        get => _passThrough ??= ParsePassThrough(Environment.GetEnvironmentVariable(PassThroughVariable));
        set => _passThrough = value;
    }

    /// <summary>
    /// Restores the MSBuild location variables (see <see cref="MsBuildLocatorGuard"/>) and removes the
    /// variables child processes must not inherit. Returns the removed names, sorted.
    /// </summary>
    public static IReadOnlyList<string> Prepare(ProcessStartInfo psi)
    {
        MsBuildLocatorGuard.RestoreOriginalEnvironment(psi);
        DisableDiffTools(psi.Environment);
        return Sanitize(psi.Environment, PassThrough);
    }

    /// <summary>Removes the variables child processes must not inherit unless <paramref name="passThrough"/> names them.</summary>
    public static IReadOnlyList<string> Sanitize(IDictionary<string, string?> environment, IReadOnlyList<string> passThrough)
    {
        var removed = new List<string>();
        foreach (var name in environment.Keys.ToList())
        {
            if (!Matches(name, passThrough) && IsRemoved(name))
            {
                _ = environment.Remove(name);
                removed.Add(name);
            }
        }

        // dotnet watch loads its hot reload agent into every process that inherits the hook; keep any other hook.
        if (environment.TryGetValue(StartupHooks, out var hooks) && hooks is not null && !Matches(StartupHooks, passThrough))
        {
            var all = hooks.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            var kept = all.Where(hook => !IsHotReloadHook(hook)).ToArray();
            if (kept.Length == 0)
            {
                _ = environment.Remove(StartupHooks);
                removed.Add(StartupHooks);
            }
            else if (kept.Length != all.Length)
            {
                environment[StartupHooks] = string.Join(Path.PathSeparator, kept);
                removed.Add(StartupHooks + " (hot reload hook)");
            }
        }

        removed.Sort(StringComparer.OrdinalIgnoreCase);
        return removed;
    }

    /// <summary>
    /// Stops snapshot libraries (Verify's DiffEngine) opening a diff tool for every failing snapshot test:
    /// Piston runs tests in the background on every save. A value the user set is kept.
    /// </summary>
    internal static void DisableDiffTools(IDictionary<string, string?> environment) =>
        _ = environment.TryAdd(DiffEngineDisabled, "true");

    /// <summary>One line for a run log naming the removed variables, or <see langword="null"/> when none were removed.</summary>
    public static string? Describe(IReadOnlyList<string> removed) =>
        removed.Count == 0
            ? null
            : $"Environment: not inherited: {string.Join(", ", removed)}. Keep one with {PassThroughVariable}.";

    internal static IReadOnlyList<string> ParsePassThrough(string? value) =>
        (value ?? string.Empty)
            .Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // "Keep everything" would switch the cleanup off by accident; name the variables instead.
            .Where(pattern => pattern != "*")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsRemoved(string name) =>
        !KeptNames.Contains(name)
        && (RemovedNames.Contains(name)
            || RemovedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    private static bool IsHotReloadHook(string hook) =>
        hook.Contains("DeltaApplier", StringComparison.OrdinalIgnoreCase)
        || hook.Contains("HotReload", StringComparison.OrdinalIgnoreCase);

    private static bool Matches(string name, IReadOnlyList<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = pattern.EndsWith('*')
                ? name.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
                : name.Equals(pattern, StringComparison.OrdinalIgnoreCase);
            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
