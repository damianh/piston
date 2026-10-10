using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;

namespace Piston.Engine.Tests;

internal sealed record TestProcessResult(int ExitCode, string Output);

internal static class TestProcess
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    public static async Task<int> RunDotnetAsync(string args, string workDir)
    {
        var result = await RunDotnetResultAsync(args, workDir);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"dotnet {args} failed with exit code {result.ExitCode} in '{workDir}'.\n{result.Output}");
        return result.ExitCode;
    }

    public static Task<TestProcessResult> RunDotnetResultAsync(string args, string workDir)
    {
        var startInfo = new ProcessStartInfo("dotnet", args)
        {
            WorkingDirectory = workDir,
        };
        // Reused MSBuild nodes can outlive dotnet and keep its redirected pipes open.
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        return RunAsync(startInfo, DefaultTimeout);
    }

    public static async Task<TestProcessResult> RunAsync(ProcessStartInfo startInfo, TimeSpan timeout)
    {
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        using var process = new Process { StartInfo = startInfo };
        var stdout = new ConcurrentQueue<string>();
        var stderr = new ConcurrentQueue<string>();
        process.OutputDataReceived += (_, e) => Capture(stdout, "stdout", e.Data);
        process.ErrorDataReceived += (_, e) => Capture(stderr, "stderr", e.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            var exitState = process.HasExited ? $"exited with code {process.ExitCode}" : "still running";
            var cleanup = "Process tree terminated.";
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or TimeoutException)
            {
                cleanup = $"Process cleanup failed: {ex.Message}";
            }

            throw new TimeoutException(
                $"Process timed out after {timeout.TotalSeconds:g}s: {startInfo.FileName} " +
                $"{(startInfo.ArgumentList.Count > 0 ? string.Join(" ", startInfo.ArgumentList) : startInfo.Arguments)}\n" +
                $"PID: {process.Id}; Working directory: {startInfo.WorkingDirectory}; Exit state: {exitState}\n" +
                $"{cleanup}\nLast output:\n{GetOutput()}");
        }

        return new TestProcessResult(process.ExitCode, GetOutput());

        string GetOutput() => string.Join(Environment.NewLine, stdout.Concat(stderr));

        static void Capture(ConcurrentQueue<string> output, string stream, string? line)
        {
            if (line is null)
                return;
            output.Enqueue($"[{stream}] {line}");
            while (output.Count > 50)
                output.TryDequeue(out _);
        }
    }
}
