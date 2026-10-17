// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Piston.Engine.Services;
using Xunit;

namespace Piston.Engine.Tests.Services;

public sealed class RedirectedProcessDrainTests
{
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Launcher_UsesPistonsRuntimeAndPreservesTheTargetCommand(bool useArgumentList)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            CreateNoWindow = true,
        };
        string[] arguments = ["test", "--filter", "Name=Test with spaces"];
        if (useArgumentList)
        {
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }
        else
        {
            start.Arguments = "test --filter \"Name=Test with spaces\"";
        }

        var targetArguments = start.Arguments;
        var targetPath = Path.Combine(Path.GetTempPath(), "another-dotnet-installation");
        start.Environment["PATH"] = targetPath;
        using var ownedProcess = new OwnedTestProcess(start);
        var launcher = ownedProcess.Process.StartInfo;
        var runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent!.Parent!.Parent!;
        var host = Path.Combine(runtimeRoot.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var child = ProcessHost.Program.CreateChildStartInfo([.. launcher.ArgumentList.Skip(2)]);

        Assert.Equal(host, launcher.FileName);
        Assert.True(File.Exists(launcher.FileName));
        Assert.Equal(targetPath, launcher.Environment["PATH"]);
        Assert.Equal("dotnet", child.FileName);
        Assert.Equal(targetArguments, child.Arguments);
        Assert.Equal(useArgumentList ? arguments : [], child.ArgumentList);
        Assert.True(child.CreateNoWindow);
        Assert.False(child.UseShellExecute);
    }

    [Fact]
    public async Task WaitForExitAndDrainAsync_CutsOffOutputHeldOpenByChildProcess()
    {
        using var ownedProcess = new OwnedTestProcess(StartInfo("--spawn-output-holder"));
        var process = ownedProcess.Process;
        var holderId = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) =>
        {
            if (int.TryParse(e.Data, out var id))
            {
                _ = holderId.TrySetResult(id);
            }
        };
        ownedProcess.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await RedirectedProcessDrain.WaitForProcessExitAsync(process, _ct).WaitAsync(TimeSpan.FromSeconds(15), _ct);
        var childId = await holderId.Task.WaitAsync(TimeSpan.FromSeconds(15), _ct);
        string? timeoutMessage = null;
        var stopwatch = Stopwatch.StartNew();
        var drained = await RedirectedProcessDrain.WaitForExitAndDrainAsync(
            ownedProcess,
            TimeSpan.FromMilliseconds(100),
            _ct,
            message => timeoutMessage = message);

        Assert.False(drained);
        Assert.True(process.HasExited);
        Assert.Contains("output was cut off", timeoutMessage, StringComparison.Ordinal);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(3));
        try
        {
            using var holder = Process.GetProcessById(childId);
            Assert.True(holder.HasExited);
        }
        catch (ArgumentException)
        {
            // The OS has already reaped the terminated output holder.
        }
    }

    [Fact]
    public async Task KillAndWaitAsync_ImmediatelyAfterStart_TerminatesTheHost()
    {
        using var ownedProcess = new OwnedTestProcess(StartInfo("--hold-output"));
        ownedProcess.Start();
        ownedProcess.Process.BeginOutputReadLine();
        ownedProcess.Process.BeginErrorReadLine();

        await RedirectedProcessDrain.KillAndWaitAsync(ownedProcess, _ct, message => Assert.Fail(message));

        Assert.True(ownedProcess.Process.HasExited);
    }

    [Fact]
    public async Task WaitForExitAndDrainAsync_PreservesNormalOutputAndExitCode()
    {
        using var ownedProcess = new OwnedTestProcess(StartInfo("--write-output"));
        var process = ownedProcess.Process;
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.Enqueue(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.Enqueue(e.Data);
            }
        };
        ownedProcess.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var drained = await RedirectedProcessDrain.WaitForExitAndDrainAsync(
            ownedProcess, TimeSpan.FromMilliseconds(100), _ct, message => Assert.Fail(message));

        Assert.True(drained);
        Assert.Equal(7, process.ExitCode);
        Assert.Equal(["stdout"], stdout);
        Assert.Equal(["stderr"], stderr);
    }

    private static ProcessStartInfo StartInfo(string mode)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(typeof(TestProcessHost.Program).Assembly.Location);
        start.ArgumentList.Add(mode);
        return start;
    }
}
