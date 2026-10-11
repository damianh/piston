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

    private string Budget(string json)
    {
        var path = Path.Combine(_root, "budget.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Budget_Missing_DefaultsToLogicalProcessors_WithoutMemoryBudget()
    {
        var budget = ExecutionBudget.Load(Path.Combine(_root, "missing.json"));
        Assert.Equal(new ExecutionBudget(Environment.ProcessorCount, null, ExecutionBudget.DefaultSource), budget);
    }

    [Fact]
    public void Budget_ParsesValues_CaseInsensitively()
    {
        var path = Budget("""{ "CpuSlots": 3, "memoryMB": 4096 }""");
        Assert.Equal(new ExecutionBudget(3, 4096, path), ExecutionBudget.Load(path));
    }

    [Fact]
    public void Budget_CpuOnly_LeavesMemoryUnbudgeted()
    {
        var path = Budget("""{ "cpuSlots": 2 }""");
        Assert.Equal(new ExecutionBudget(2, null, path), ExecutionBudget.Load(path));
    }

    [Theory]
    [InlineData("""{ "cpuSlots": 0 }""", "cpuSlots")]
    [InlineData("""{ "cpuSlots": -1 }""", "cpuSlots")]
    [InlineData("""{ "cpuSlots": 1.5 }""", "cpuSlots")]
    [InlineData("""{ "cpuSlots": "4" }""", "cpuSlots")]
    [InlineData("""{ "memoryMb": 0 }""", "memoryMb")]
    [InlineData("""{ "cpuSlots": 2, "maxRuns": 1 }""", "unknown property 'maxRuns'")]
    [InlineData("""[ 1 ]""", "JSON object")]
    [InlineData("""{ "cpuSlots": """, "budget")]
    public void Budget_Invalid_IsExplicitError(string json, string expected)
    {
        var path = Budget(json);
        var ex = Assert.Throws<ExecutionCoordinationException>(() => ExecutionBudget.Load(path));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.Contains(path, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reservation_LargerThanCpuBudget_CanNeverBeAdmitted()
    {
        var ex = Assert.Throws<ExecutionCoordinationException>(
            () => new ExecutionReservation(5, null).EnsureAdmissible(new ExecutionBudget(4, null, "test")));
        Assert.Contains("never be admitted", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reservation_WithoutMemory_IsRejected_WhenBudgetLimitsMemory()
    {
        var ex = Assert.Throws<ExecutionCoordinationException>(
            () => new ExecutionReservation(1, null).EnsureAdmissible(new ExecutionBudget(4, 1024, "test")));
        Assert.Contains("execution.memoryMb", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reservation_LargerThanMemoryBudget_IsRejected() => _ = Assert.Throws<ExecutionCoordinationException>(
            () => new ExecutionReservation(1, 2048).EnsureAdmissible(new ExecutionBudget(4, 1024, "test")));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Reservation_NonPositive_IsRejected(int memory) => _ = Assert.Throws<ExecutionCoordinationException>(
            () => new ExecutionReservation(1, memory).EnsureAdmissible(new ExecutionBudget(4, null, "test")));

    [Fact]
    public void Reservation_WithMemory_IsAdmissible_WhenMemoryIsNotBudgeted() => new ExecutionReservation(4, 512).EnsureAdmissible(new ExecutionBudget(4, null, "test"));

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

        Assert.True(Directory.Exists(dir.QueueDirectory));
        Assert.True(Directory.Exists(dir.LeasesDirectory));
        Assert.True(Directory.Exists(dir.OwnersDirectory));
        Assert.Empty(Directory.GetFiles(dir.StateDirectory, "lock-probe-*"));
        if (!OperatingSystem.IsWindows())
        {
            var expected = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            Assert.Equal(expected, File.GetUnixFileMode(dir.BaseDirectory));
            Assert.Equal(expected, File.GetUnixFileMode(dir.QueueDirectory));
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
