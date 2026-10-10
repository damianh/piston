using System.Diagnostics;
using Xunit;

namespace Piston.Engine.Tests;

public sealed class TestProcessTests
{
    [Fact]
    public async Task RunAsyncDrainsBothStreamsAndBoundsCapturedOutput()
    {
        var startInfo = Shell(
            "for /L %i in (1,1,2000) do @(echo out-%i & echo err-%i 1>&2)",
            "i=0; while [ \"$i\" -lt 2000 ]; do echo out-$i; echo err-$i >&2; i=$((i+1)); done");

        var result = await TestProcess.RunAsync(startInfo, TimeSpan.FromSeconds(15));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("[stdout]", result.Output);
        Assert.Contains("[stderr]", result.Output);
        Assert.InRange(result.Output.Split(Environment.NewLine).Length, 1, 100);
    }

    [Fact]
    public async Task RunAsyncTimeoutKillsProcessAndReportsCommandDirectoryAndOutput()
    {
        var startInfo = Shell(
            "echo timeout-marker & echo error-marker 1>&2 & ping -n 30 127.0.0.1 > nul",
            "echo timeout-marker; echo error-marker >&2; sleep 30 & echo child:$!; wait");
        startInfo.WorkingDirectory = Directory.GetCurrentDirectory();
        var sw = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            TestProcess.RunAsync(startInfo, TimeSpan.FromSeconds(2)));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), error.Message);
        Assert.Contains("timed out after 2s", error.Message);
        Assert.Contains(startInfo.FileName, error.Message);
        Assert.Contains(startInfo.WorkingDirectory, error.Message);
        Assert.Contains("[stdout] timeout-marker", error.Message);
        Assert.Contains("[stderr] error-marker", error.Message);
        Assert.Contains("Process tree terminated.", error.Message);
        var pidLine = error.Message.Split('\n').Single(line => line.StartsWith("PID:"));
        AssertProcessExited(int.Parse(pidLine.Split(';')[0]["PID: ".Length..]));
        if (!OperatingSystem.IsWindows())
        {
            var childLine = error.Message.Split('\n').Single(line => line.StartsWith("[stdout] child:"));
            AssertProcessExited(int.Parse(childLine["[stdout] child:".Length..]));
        }
    }

    [Fact]
    public async Task RunDotnetAsyncParallelMsBuildNodesCompleteWithoutRetainingOutputPipes()
    {
        var root = Directory.CreateTempSubdirectory("piston-node-reuse-test-").FullName;
        try
        {
            foreach (var name in new[] { "First", "Second" })
            {
                await File.WriteAllTextAsync(Path.Combine(root, $"{name}.proj"), $"""
                    <Project>
                      <Target Name="Build">
                        <Error Condition="'$(MSBUILDDISABLENODEREUSE)' != '1'" Text="Node reuse guard is missing." />
                        <Message Text="{name} completed" Importance="high" />
                      </Target>
                    </Project>
                    """);
            }
            await File.WriteAllTextAsync(Path.Combine(root, "All.proj"), """
                <Project>
                  <Target Name="Build">
                    <MSBuild Projects="First.proj;Second.proj" BuildInParallel="true" />
                  </Target>
                </Project>
                """);

            for (var run = 0; run < 2; run++)
            {
                var result = await TestProcess.RunDotnetResultAsync("msbuild All.proj -m:2 -nologo", root);
                Assert.True(result.ExitCode == 0, result.Output);
                Assert.Contains("First completed", result.Output);
                Assert.Contains("Second completed", result.Output);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsyncReturnsNonzeroExitAndDiagnostics()
    {
        var result = await TestProcess.RunAsync(Shell("echo failed 1>&2 & exit /b 7", "echo failed >&2; exit 7"),
            TimeSpan.FromSeconds(10));

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("[stderr] failed", result.Output);
    }

    [Fact]
    public async Task RunDotnetAsyncRejectsNonzeroExitWithDiagnostics()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TestProcess.RunDotnetAsync("piston-nonexistent-command", Directory.GetCurrentDirectory()));

        Assert.Contains("exit code", error.Message);
        Assert.Contains("piston-nonexistent-command", error.Message);
        Assert.Contains("[", error.Message);
    }

    private static ProcessStartInfo Shell(string windowsCommand, string unixCommand)
    {
        var startInfo = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? "/c" : "-c");
        startInfo.ArgumentList.Add(OperatingSystem.IsWindows() ? windowsCommand : unixCommand);
        return startInfo;
    }

    private static void AssertProcessExited(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            Assert.True(process.HasExited, $"Process {pid} survived timeout cleanup.");
        }
        catch (ArgumentException)
        {
            // The OS has already reaped the process.
        }
    }
}
