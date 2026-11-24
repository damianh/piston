// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;
using Xunit;

namespace Piston.Engine.Tests.Execution;

public sealed class ExecutionConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"piston-exec-config-{Guid.NewGuid():N}");

    public ExecutionConfigurationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string WriteFile(string json)
    {
        var path = Path.Combine(_root, "budget.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Lock_ReadOnlyFileOnWindows_IsAnAccessError_NotContention()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = WriteFile("{}");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            _ = Assert.Throws<UnauthorizedAccessException>(() => ExclusiveFileLock.TryAcquire(path, FileMode.Open));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void EnsureReady_TightensExistingUnixDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        var paths = new[] { dir.BaseDirectory, dir.StateDirectory, dir.OwnersDirectory, dir.ResourceQueueDirectory, dir.ResourceLeasesDirectory };
        foreach (var path in paths)
        {
            _ = Directory.CreateDirectory(path);
            File.SetUnixFileMode(path, (UnixFileMode)511);
        }

        dir.EnsureReady();

        foreach (var path in paths)
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void Directory_RelativePath_IsRejected() => _ = Assert.Throws<ExecutionCoordinationException>(() => new ExecutionCoordinationDirectory("relative/dir"));

    [Fact]
    public void ResolveDefault_UsesAbsoluteEnvironmentOverride()
    {
        var dir = ExecutionCoordinationDirectory.ResolveDefault(
            name => name == ExecutionCoordinationDirectory.EnvironmentVariable ? _root : null,
            () => throw new InvalidOperationException("must not be used"));
        Assert.Equal(Path.GetFullPath(_root), dir.BaseDirectory);
    }

    [Fact]
    public void ResolveDefault_RelativeEnvironmentOverride_IsExplicitError()
    {
        var ex = Assert.Throws<ExecutionCoordinationException>(() => ExecutionCoordinationDirectory.ResolveDefault(
            _ => "relative", () => _root));
        Assert.Contains(ExecutionCoordinationDirectory.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDefault_UsesPerUserLocalApplicationData()
    {
        var dir = ExecutionCoordinationDirectory.ResolveDefault(_ => null, () => _root);
        Assert.Equal(Path.Combine(_root, "Piston", "execution"), dir.BaseDirectory);
    }

    [Fact]
    public void ResolveDefault_WithoutLocalApplicationData_IsExplicitError()
    {
        var ex = Assert.Throws<ExecutionCoordinationException>(
            () => ExecutionCoordinationDirectory.ResolveDefault(_ => null, () => ""));
        Assert.Contains(ExecutionCoordinationDirectory.EnvironmentVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureReady_CreatesUserOnlyDirectories()
    {
        var dir = new ExecutionCoordinationDirectory(Path.Combine(_root, "coordination"));
        dir.EnsureReady();
        dir.EnsureReady();

        Assert.True(Directory.Exists(dir.ResourceQueueDirectory));
        Assert.True(Directory.Exists(dir.ResourceLeasesDirectory));
        Assert.True(Directory.Exists(dir.OwnersDirectory));
        Assert.Empty(Directory.GetFiles(dir.StateDirectory, "lock-probe-*"));
        if (!OperatingSystem.IsWindows())
        {
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expected, File.GetUnixFileMode(dir.BaseDirectory));
            Assert.Equal(expected, File.GetUnixFileMode(dir.ResourceQueueDirectory));
        }
    }

    [Fact]
    public void EnsureReady_FailsExplicitly_WhenFileLockingIsDisabled()
    {
        // Windows sharing modes cannot be disabled by this switch; it only affects Unix advisory locks.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var dir = Path.Combine(_root, "coordination");
        using var probe = LeaseHolderProcess.Start(
            new Dictionary<string, string> { ["DOTNET_SYSTEM_IO_DISABLEFILELOCKING"] = "1" }, "probe", dir);

        var line = probe.WaitForLine("OK", "ERROR");

        Assert.StartsWith("ERROR", line, StringComparison.Ordinal);
        Assert.Contains("not enforced", line, StringComparison.Ordinal);
        Assert.Equal(2, probe.WaitForExit());
    }

    [Fact]
    public void EnsureReady_SucceedsInAnotherProcess_WithLockingEnabled()
    {
        using var probe = LeaseHolderProcess.Start("probe", Path.Combine(_root, "coordination"));
        Assert.Equal("OK", probe.WaitForLine("OK", "ERROR"));
        Assert.Equal(0, probe.WaitForExit());
    }
}
