// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Xunit;

namespace Piston.Tests;

public sealed class RuntimeStagingTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-runtime-staging-").FullName;
    private readonly string _source;
    private readonly string _runtimes;

    public RuntimeStagingTests()
    {
        _source = Directory.CreateDirectory(Path.Combine(_root, "store", "tools")).FullName;
        File.WriteAllText(Path.Combine(_source, "Piston.dll"), "dll");
        _ = Directory.CreateDirectory(Path.Combine(_source, "wwwroot"));
        File.WriteAllText(Path.Combine(_source, "wwwroot", "index.html"), "<html />");
        _runtimes = Path.Combine(_root, "runtimes");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Stage_CopiesThePayloadAndMarksItComplete()
    {
        var destination = RuntimeStaging.Stage(_source, Path.Combine(_runtimes, "1.0.0-abc"));

        Assert.Equal("dll", File.ReadAllText(Path.Combine(destination, "Piston.dll")));
        Assert.Equal("<html />", File.ReadAllText(Path.Combine(destination, "wwwroot", "index.html")));
        Assert.True(File.Exists(Path.Combine(destination, RuntimeStaging.Marker)));
        Assert.Equal([destination], Directory.GetDirectories(_runtimes));
    }

    [Fact]
    public void ResolveRuntimeRoot_UsesOverrideOrPerUserDataAndFailsWithoutEither()
    {
        Assert.Equal(Path.Combine("override", "Piston", "runtimes"),
            RuntimeStaging.ResolveRuntimeRoot("override", "user-data"));
        Assert.Equal(Path.Combine("user-data", "Piston", "runtimes"),
            RuntimeStaging.ResolveRuntimeRoot(null, "user-data"));
        _ = Assert.Throws<IOException>(() => RuntimeStaging.ResolveRuntimeRoot(null, null));
    }

    [Fact]
    public void GetDotnetHostPath_ResolvesTheRuntimeInstallation()
    {
        var host = RuntimeStaging.GetDotnetHostPath();

        Assert.True(File.Exists(host));
        Assert.Equal(OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet", Path.GetFileName(host));
    }

    [Fact]
    public async Task Run_FailsWhenUnixFileLockingIsDisabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var stale = StageAged("stale", DateTime.UtcNow - TimeSpan.FromDays(30));
        var start = new ProcessStartInfo(RuntimeStaging.GetDotnetHostPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Piston.dll"));
        start.ArgumentList.Add(RuntimeStaging.Command);
        start.ArgumentList.Add(_source);
        start.ArgumentList.Add(Path.Combine(_runtimes, "test"));
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Piston.");
        var output = process.StandardOutput.ReadToEndAsync(_ct);
        var error = process.StandardError.ReadToEndAsync(_ct);
        await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(30), _ct);

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("not enforced", await error, StringComparison.Ordinal);
        Assert.Empty(await output);
        Assert.False(Directory.Exists(Path.Combine(_runtimes, "test")));
        Assert.True(Directory.Exists(stale));
    }

    [Fact]
    public async Task RunFromInstalledRuntime_FailsWhenUnixFileLockingIsDisabled()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runtime = RuntimeStaging.Stage(AppContext.BaseDirectory, Path.Combine(_runtimes, "installed"));
        var start = new ProcessStartInfo(RuntimeStaging.GetDotnetHostPath())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(runtime, "Piston.dll"));
        start.ArgumentList.Add("--version");
        start.Environment["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Piston.");
        var output = process.StandardOutput.ReadToEndAsync(_ct);
        var error = process.StandardError.ReadToEndAsync(_ct);
        await process.WaitForExitAsync(_ct).WaitAsync(TimeSpan.FromSeconds(30), _ct);

        Assert.Equal(1, process.ExitCode);
        Assert.Contains("cannot claim the runtime copy", await error, StringComparison.Ordinal);
        Assert.Empty(await output);
        Assert.True(File.Exists(Path.Combine(runtime, RuntimeStaging.Marker)));
    }

    [Fact]
    public void Stage_LeavesACompleteRuntimeUntouched()
    {
        var destination = RuntimeStaging.Stage(_source, Path.Combine(_runtimes, "1.0.0-abc"));
        File.WriteAllText(Path.Combine(_source, "Piston.dll"), "changed");

        _ = RuntimeStaging.Stage(_source, destination);

        Assert.Equal("dll", File.ReadAllText(Path.Combine(destination, "Piston.dll")));
    }

    [Fact]
    public void Stage_ReplacesAnIncompleteDestination()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_runtimes, "1.0.0-abc")).FullName;
        File.WriteAllText(Path.Combine(destination, "partial.dll"), "");

        _ = RuntimeStaging.Stage(_source, destination);

        Assert.False(File.Exists(Path.Combine(destination, "partial.dll")));
        Assert.True(File.Exists(Path.Combine(destination, "Piston.dll")));
    }

    [Fact]
    public async Task Stage_ConcurrentStartsAllSucceedWithOneCopy()
    {
        var destination = Path.Combine(_runtimes, "1.0.0-abc");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => RuntimeStaging.Stage(_source, destination), TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.Equal(destination, result));
        Assert.Equal([destination], Directory.GetDirectories(_runtimes));
        Assert.True(File.Exists(Path.Combine(destination, "Piston.dll")));
    }

    [Fact]
    public async Task Stage_ConcurrentStartsRepairAnIncompleteDestinationOnce()
    {
        var destination = Directory.CreateDirectory(Path.Combine(_runtimes, "1.0.0-abc")).FullName;
        File.WriteAllText(Path.Combine(destination, "partial.dll"), "");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => RuntimeStaging.Stage(_source, destination), TestContext.Current.CancellationToken)));

        Assert.All(results, result => Assert.Equal(destination, result));
        Assert.Equal([destination], Directory.GetDirectories(_runtimes));
        Assert.False(File.Exists(Path.Combine(destination, "partial.dll")));
        Assert.True(File.Exists(Path.Combine(destination, "Piston.dll")));
    }

    [Fact]
    public void Stage_ClaimsAnExistingRuntimeSoCleanupKeepsIt()
    {
        var now = DateTime.UtcNow;
        var runtime = StageAged("1.0.0-abc", now - TimeSpan.FromDays(30));

        _ = RuntimeStaging.Stage(_source, runtime);
        RuntimeStaging.Cleanup(_runtimes, "other", now);

        Assert.True(File.Exists(Path.Combine(runtime, "Piston.dll")));
    }

    [Fact]
    public void Run_ReportsUsageAndMissingPayloads()
    {
        var error = new StringWriter();

        Assert.Equal(2, RuntimeStaging.Run(["only-one"], error));
        Assert.Equal(1, RuntimeStaging.Run([Path.Combine(_root, "missing"), Path.Combine(_runtimes, "x")], error));
        Assert.Contains("Piston.dll", error.ToString());
        Assert.Equal(0, RuntimeStaging.Run([_source, Path.Combine(_runtimes, "1.0.0-abc")], error));
    }

    [Fact]
    public void Cleanup_RemovesOnlyStaleUnusedRuntimes()
    {
        var now = DateTime.UtcNow;
        var current = StageAged("current", now - TimeSpan.FromDays(30));
        var recent = StageAged("recent", now - TimeSpan.FromDays(1));
        var stale = StageAged("stale", now - TimeSpan.FromDays(30));
        var inUse = StageAged("in-use", now - TimeSpan.FromDays(30));
        using var activeLease = RuntimeStaging.AcquireLease(inUse);
        File.SetLastWriteTimeUtc(Path.Combine(inUse, RuntimeStaging.LastUsed), now - TimeSpan.FromDays(30));
        var abandoned = Directory.CreateDirectory(Path.Combine(_runtimes, "x.0123.staging")).FullName;
        Directory.SetLastWriteTimeUtc(abandoned, now - TimeSpan.FromDays(1));
        var inProgress = Directory.CreateDirectory(Path.Combine(_runtimes, "y.4567.staging")).FullName;
        var unmarked = Directory.CreateDirectory(Path.Combine(_runtimes, "unmarked")).FullName;

        RuntimeStaging.Cleanup(_runtimes, Path.GetFileName(current), now);

        Assert.Equal(
            new[] { current, inProgress, inUse, recent, unmarked }.Order(StringComparer.Ordinal),
            Directory.GetDirectories(_runtimes).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(stale));
        Assert.False(Directory.Exists(abandoned));
    }

    private string StageAged(string name, DateTime lastUsed)
    {
        var runtime = RuntimeStaging.Stage(_source, Path.Combine(_runtimes, name));
        var file = Path.Combine(runtime, RuntimeStaging.LastUsed);
        File.WriteAllText(file, "");
        File.SetLastWriteTimeUtc(file, lastUsed);
        return runtime;
    }
}
