// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Runtime.InteropServices;
using Piston.Engine.Coverage;
using Xunit;

namespace Piston.Engine.Tests.Coverage;

public sealed class CoverageOutputIsolationTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _root = Directory.CreateTempSubdirectory("piston-output-isolation-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void SeparateOrdinaryOutputsCanRunConcurrently()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "The output link check is not supported on this operating system.");
        var (first, second) = Outputs();
        File.WriteAllText(Path.Combine(first, "one.dll"), "one");
        File.WriteAllText(Path.Combine(second, "two.dll"), "two");

        Assert.True(CoverageOutputIsolation.CanRunConcurrently([first, second], out var reason, _ct), reason);
    }

    [Fact]
    public void RepeatedDirectoryIdentityForcesSerialMeasurements()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "The output link check is not supported on this operating system.");
        var (first, _) = Outputs();

        Assert.False(CoverageOutputIsolation.CanRunConcurrently([first, first], out var reason, _ct));
        Assert.Contains("repeated", reason);
    }

    [Fact]
    public void HardLinkedFilesForceSerialMeasurements()
    {
        var (first, second) = Outputs();
        var target = Path.Combine(first, "one.dll");
        File.WriteAllText(target, "one");
        CreateHardLinkForTest(Path.Combine(second, "other-name.dll"), target);

        Assert.False(CoverageOutputIsolation.CanRunConcurrently([first, second], out var reason, _ct));
        Assert.Contains("links", reason);
    }

    internal static void CreateHardLinkForTest(string alias, string target)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS(),
            "The hard-link test is not supported on this operating system.");
        var created = OperatingSystem.IsWindows()
            ? CreateHardLink(alias, target, IntPtr.Zero)
            : OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()
                ? Link(target, alias) == 0
                : false;
        Assert.True(created, new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymbolicLinksInsideAnOutputForceSerialMeasurements(bool directory)
    {
        var (first, second) = Outputs();
        var target = directory ? Directory.CreateDirectory(Path.Combine(first, "nested")).FullName : Path.Combine(first, "one.dll");
        if (!directory)
        {
            File.WriteAllText(target, "one");
        }

        try
        {
            if (directory)
            {
                _ = Directory.CreateSymbolicLink(Path.Combine(second, "linked"), target);
            }
            else
            {
                _ = File.CreateSymbolicLink(Path.Combine(second, "linked.dll"), target);
            }
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            Assert.Skip("Creating symbolic links is not permitted on this Windows machine.");
        }

        Assert.False(CoverageOutputIsolation.CanRunConcurrently([first, second], out var reason, _ct));
        Assert.Contains("symbolic link", reason);
    }

    [Fact]
    public void MissingOutputForcesSerialMeasurements()
    {
        var (first, second) = Outputs();
        Directory.Delete(second);

        Assert.False(CoverageOutputIsolation.CanRunConcurrently([first, second], out var reason, _ct));
        Assert.NotNull(reason);
    }

    [Fact]
    public void CancelledInspectionStopsInsteadOfScanningOutputs()
    {
        var (first, second) = Outputs();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_ct);
        stop.Cancel();

        _ = Assert.Throws<OperationCanceledException>(
            () => CoverageOutputIsolation.CanRunConcurrently([first, second], out _, stop.Token));
    }

    private (string First, string Second) Outputs() =>
        (Directory.CreateDirectory(Path.Combine(_root, "first")).FullName,
         Directory.CreateDirectory(Path.Combine(_root, "second")).FullName);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int Link(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string target, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string target, IntPtr securityAttributes);
}
