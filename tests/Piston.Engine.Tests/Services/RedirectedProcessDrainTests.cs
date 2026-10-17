// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class RedirectedProcessDrainTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task WaitForExitAndDrainAsync_CutsOffOutputHeldOpenByChildProcess()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c start /b ping -n 2 127.0.0.1")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 1 & exit 0\"");
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        using var process = new Process { StartInfo = startInfo };
        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        string? timeoutMessage = null;
        var stopwatch = Stopwatch.StartNew();
        var drained = await RedirectedProcessDrain.WaitForExitAndDrainAsync(
            process,
            TimeSpan.FromMilliseconds(100),
            _ct,
            message => timeoutMessage = message);

        Assert.False(drained);
        Assert.True(process.HasExited);
        Assert.Contains("output was cut off", timeoutMessage, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
    }
}
