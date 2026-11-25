// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Services;
using Piston.TestProcessHost;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class ClrMdHangDiagnosticsTests : IDisposable
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"piston-hang-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureAsync_ReadsTheHungTestsStackAndDumpsItsProcess()
    {
        _ = Directory.CreateDirectory(_directory);
        var ready = Path.Combine(_directory, "ready");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(typeof(HangingFixture).Assembly.Location);
        start.ArgumentList.Add("--hang");
        start.ArgumentList.Add(ready);

        using var scope = OwnedProcessScope.Begin();
        using var process = new OwnedProcess(start);
        process.Start();
        try
        {
            while (!File.Exists(ready))
            {
                Assert.False(process.Process.HasExited, "The hanging fixture exited early.");
                await Task.Delay(50, _ct);
            }

            // A dotnet shim on PATH can start the runtime in a child process, so take the PID the fixture reports.
            var pid = int.Parse(await File.ReadAllTextAsync(ready, _ct), System.Globalization.CultureInfo.InvariantCulture);

            var staging = Path.Combine(_directory, "staging");
            var diagnostics = new ClrMdHangDiagnostics(staging);
            var result = await diagnostics.CaptureAsync(
                new HangCapture(
                    "Piston.TestProcessHost.csproj",
                    "HangingFixture.Hang",
                    $"{typeof(HangingFixture).FullName}.{nameof(HangingFixture.Hang)}",
                    scope.Processes),
                _ct);

            Assert.NotNull(result);
            TestContext.Current.TestOutputHelper?.WriteLine(result.Stacks ?? "");
            Assert.Contains("HangingFixture.Hang", result.Stacks, StringComparison.Ordinal);
            Assert.Contains($"Process {pid}", result.Stacks, StringComparison.Ordinal);
            Assert.Collection(result.Attachments,
                stacks =>
                {
                    Assert.Equal("hang-stacks.txt", stacks.DisplayName);
                    Assert.Contains("HangingFixture.Hang", File.ReadAllText(stacks.Path!), StringComparison.Ordinal);
                },
                dump =>
                {
                    Assert.Equal($"hang-{pid}.dmp", dump.DisplayName);
                    Assert.True(new FileInfo(dump.Path!).Length > 0);
                    Assert.True(dump.MoveOnRetain);
                });
        }
        finally
        {
            _ = process.KillProcessTree();
        }
    }

    [Fact]
    public async Task CaptureAsync_WithoutDotNetProcesses_ReturnsNothing()
    {
        var diagnostics = new ClrMdHangDiagnostics(_directory, publishedProcesses: () => [Environment.ProcessId]);

        var result = await diagnostics.CaptureAsync(new HangCapture("T.csproj", "T.Hang", null, []), _ct);

        Assert.Null(result);
    }
}
