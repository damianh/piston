// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class ProcessTerminationTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task KillAndWaitAsync_BoundsWaitWhenChildKeepsOutputOpen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add("sleep 30 & echo $!; exit 0");

        var output = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (int.TryParse(e.Data, out var childPid))
            {
                _ = output.TrySetResult(childPid);
            }
        };

        _ = process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var child = await output.Task.WaitAsync(TimeSpan.FromSeconds(5), _ct);
        try
        {
            Assert.True(process.WaitForExit(5_000));

            var messages = new List<string>();
            var stopwatch = Stopwatch.StartNew();
            await ProcessTermination.KillAndWaitAsync(process, TimeSpan.FromMilliseconds(200), messages.Add);

            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
            Assert.Contains(messages, message => message.Contains("finish draining redirected output", StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                using var childProcess = Process.GetProcessById(child);
                if (!childProcess.HasExited)
                {
                    childProcess.Kill();
                    _ = childProcess.WaitForExit(5_000);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
            }
        }
    }
}
