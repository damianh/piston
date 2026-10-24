// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class ChildProcessEnvironmentTests
{
    [Fact]
    public void Sanitize_RemovesListenAddressWatchAndAspireRunVariables()
    {
        var environment = Environment(
            ("ASPNETCORE_URLS", "http://localhost:5199"),
            ("DOTNET_HTTP_PORTS", "5199"),
            ("DOTNET_LAUNCH_PROFILE", "Piston"),
            ("DOTNET_MODIFIABLE_ASSEMBLIES", "debug"),
            ("DOTNET_WATCH", "1"),
            ("DOTNET_WATCH_ITERATION", "2"),
            ("DOTNET_HOTRELOAD_NAMEDPIPE_NAME", "pipe"),
            ("ASPIRE_BACKCHANNEL_PATH", "/tmp/sock"),
            ("DCP_SESSION_FOLDER", "/tmp/dcp"),
            ("DEBUG_SESSION_PORT", "1234"),
            ("PATH", "/usr/bin"),
            ("DOTNET_ROOT", "/usr/share/dotnet"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector"),
            ("PISTON_EXECUTION_DIRECTORY", "/tmp/exec"));

        var removed = ChildProcessEnvironment.Sanitize(environment, []);

        Assert.Equal(
            [
                "ASPIRE_BACKCHANNEL_PATH", "ASPNETCORE_URLS", "DCP_SESSION_FOLDER", "DEBUG_SESSION_PORT",
                "DOTNET_HOTRELOAD_NAMEDPIPE_NAME", "DOTNET_HTTP_PORTS", "DOTNET_LAUNCH_PROFILE",
                "DOTNET_MODIFIABLE_ASSEMBLIES", "DOTNET_WATCH", "DOTNET_WATCH_ITERATION",
            ],
            removed);
        Assert.Equal(
            ["DOTNET_ROOT", "OTEL_EXPORTER_OTLP_ENDPOINT", "PATH", "PISTON_EXECUTION_DIRECTORY"],
            environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DisableDiffTools_SetsDiffEngineDisabled()
    {
        var environment = Environment(("PATH", "/usr/bin"));

        ChildProcessEnvironment.DisableDiffTools(environment);

        Assert.Equal("true", environment[ChildProcessEnvironment.DiffEngineDisabled]);
    }

    [Fact]
    public void DisableDiffTools_KeepsUserValue()
    {
        var environment = Environment((ChildProcessEnvironment.DiffEngineDisabled, "false"));

        ChildProcessEnvironment.DisableDiffTools(environment);

        Assert.Equal("false", environment[ChildProcessEnvironment.DiffEngineDisabled]);
    }

    [Fact]
    public void Sanitize_KeepsAspireUserSettings()
    {
        var environment = Environment(("ASPIRE_CONTAINER_RUNTIME", "podman"));

        Assert.Empty(ChildProcessEnvironment.Sanitize(environment, []));
        Assert.Equal("podman", environment["ASPIRE_CONTAINER_RUNTIME"]);
    }

    [Fact]
    public void Sanitize_KeepsPassThroughNamesAndPrefixes()
    {
        var environment = Environment(
            ("ASPNETCORE_URLS", "http://localhost:6000"),
            ("ASPIRE_DASHBOARD_URL", "http://dash"),
            ("ASPIRE_BACKCHANNEL_PATH", "/tmp/sock"));

        var removed = ChildProcessEnvironment.Sanitize(
            environment, ChildProcessEnvironment.ParsePassThrough("aspnetcore_urls; ASPIRE_DASHBOARD_*"));

        Assert.Equal(["ASPIRE_BACKCHANNEL_PATH"], removed);
        Assert.Equal(["ASPIRE_DASHBOARD_URL", "ASPNETCORE_URLS"], environment.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ParsePassThrough_IgnoresKeepEverything() => Assert.Equal(["A", "B*"], ChildProcessEnvironment.ParsePassThrough(" A ,*; B* ;a"));

    [Fact]
    public void Sanitize_DropsOnlyTheHotReloadStartupHook()
    {
        var separator = Path.PathSeparator;
        var environment = Environment(
            ("DOTNET_STARTUP_HOOKS", $"/sdk/Microsoft.Extensions.DotNetDeltaApplier.dll{separator}/app/MyHook.dll"));

        var removed = ChildProcessEnvironment.Sanitize(environment, []);

        Assert.Equal(["DOTNET_STARTUP_HOOKS (hot reload hook)"], removed);
        Assert.Equal("/app/MyHook.dll", environment["DOTNET_STARTUP_HOOKS"]);
    }

    [Fact]
    public void Sanitize_RemovesTheStartupHookVariableWhenOnlyHotReloadIsHooked()
    {
        var environment = Environment(("DOTNET_STARTUP_HOOKS", "/sdk/Microsoft.Extensions.DotNetDeltaApplier.dll"));

        Assert.Equal(["DOTNET_STARTUP_HOOKS"], ChildProcessEnvironment.Sanitize(environment, []));
        Assert.False(environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
    }

    [Fact]
    public void Describe_NamesThePassThroughVariable()
    {
        Assert.Null(ChildProcessEnvironment.Describe([]));
        var note = ChildProcessEnvironment.Describe(["ASPNETCORE_URLS"]);
        Assert.Contains("ASPNETCORE_URLS", note);
        Assert.Contains(ChildProcessEnvironment.PassThroughVariable, note);
    }

    [Fact]
    public void MtpServerHostStartInfo_DoesNotInheritDotnetWatchVariables()
    {
        const string name = "DOTNET_WATCH_PISTON_CHILD_ENVIRONMENT_TEST";
        System.Environment.SetEnvironmentVariable(name, "1");
        try
        {
            var psi = MtpServerSession.CreateStartInfo("/sln/bin/Tests.dll", "/sln", 1234);

            Assert.False(psi.Environment.ContainsKey(name));
        }
        finally
        {
            System.Environment.SetEnvironmentVariable(name, null);
        }
    }

    private static Dictionary<string, string?> Environment(params (string Name, string? Value)[] variables) =>
        variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
