// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using Piston.Engine.Execution;
using Piston.Engine.Services;

namespace Piston.ExecutionLeaseHolder;

/// <summary>
/// Holds worktree ownership or an execution admission in a separate process so tests can verify
/// cross-process exclusion and recovery after the process is killed.
/// <list type="bullet">
/// <item><c>own &lt;dir&gt; &lt;solution&gt;</c>: acquire ownership, print <c>READY</c>, wait.</item>
/// <item><c>admit &lt;dir&gt; &lt;cpu&gt; &lt;memoryMb|0&gt;</c>: print <c>QUEUED n</c> while queued, <c>READY</c> once admitted, wait.</item>
/// <item><c>own-admit &lt;dir&gt; &lt;solution&gt; &lt;cpu&gt; &lt;memoryMb|0&gt; &lt;host&gt; &lt;dashboardUrl|-&gt;</c>: own a worktree and hold a machine reservation.</item>
/// <item><c>probe &lt;dir&gt;</c>: prepare the directory and exit 0, or print the error and exit 2.</item>
/// <item><c>run[-raw] &lt;command&gt; [args]</c>: launch an owned test command and forward its output and exit code.</item>
/// <item><c>discover</c>: run best-effort test discovery and print the number of results.</item>
/// </list>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args[0] == "discover")
        {
            var tests = await TestListProcess.RunAsync(
                "--list-tests", Environment.CurrentDirectory, "Missing.csproj", "Test",
                new TestListParser(_ => true), null, CancellationToken.None);
            Console.WriteLine(tests.Count);
            return 0;
        }

        if (args[0] is "run" or "run-raw")
        {
            var start = new ProcessStartInfo(args[1])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (args[0] == "run-raw")
            {
                start.Arguments = args[2];
            }
            else
            {
                foreach (var argument in args.Skip(2))
                {
                    start.ArgumentList.Add(argument);
                }
            }

            using var owned = new OwnedTestProcess(start);
            owned.Process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Console.WriteLine(e.Data);
                }
            };
            owned.Process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Console.Error.WriteLine(e.Data);
                }
            };
            owned.Start();
            owned.Process.BeginOutputReadLine();
            owned.Process.BeginErrorReadLine();
            _ = await RedirectedProcessDrain.WaitForExitAndDrainAsync(
                owned, RedirectedProcessDrain.DefaultTimeout, CancellationToken.None, Console.Error.WriteLine);
            return owned.Process.ExitCode;
        }

        try
        {
            var directory = new ExecutionCoordinationDirectory(args[1]);
            switch (args[0])
            {
                case "own":
                    directory.EnsureReady();
                    using (WorktreeOwnership.Acquire(directory, args[2]))
                    {
                        await ReadyAndWaitAsync();
                    }

                    return 0;

                case "admit":
                    var memory = int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
                    var coordinator = new MachineExecutionCoordinator(
                        directory,
                        new ExecutionReservation(int.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture), memory == 0 ? null : memory),
                        TimeSpan.FromMilliseconds(20));
                    _ = coordinator.Initialize();
                    using (await coordinator.AdmitAsync(
                        Guid.NewGuid().ToString("N"),
                        status => Console.Out.WriteLine($"QUEUED {status.QueuedAhead}"),
                        inheritedSequence: null,
                        CancellationToken.None))
                    {
                        await ReadyAndWaitAsync();
                    }
                    return 0;

                case "own-admit":
                    directory.EnsureReady();
                    using (WorktreeOwnership.Acquire(
                        directory, args[2], args[5] == "-" ? null : args[5],
                        dashboardUrl: args[6] == "-" ? null : args[6]))
                    {
                        var ownMemory = int.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
                        var ownCoordinator = new MachineExecutionCoordinator(
                            directory,
                            new ExecutionReservation(
                                int.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture),
                                ownMemory == 0 ? null : ownMemory),
                            TimeSpan.FromMilliseconds(20));
                        _ = ownCoordinator.Initialize();
                        using (await ownCoordinator.AdmitAsync(
                            Guid.NewGuid().ToString("N"),
                            status => Console.Out.WriteLine($"QUEUED {status.QueuedAhead}"),
                            inheritedSequence: null,
                            CancellationToken.None))
                        {
                            await ReadyAndWaitAsync();
                        }
                    }
                    return 0;

                case "probe":
                    directory.EnsureReady();
                    Console.Out.WriteLine("OK");
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown mode '{args[0]}'.");
                    return 1;
            }
        }
        catch (ExecutionCoordinationException ex)
        {
            Console.Out.WriteLine($"ERROR {ex.Message}");
            return 2;
        }
    }

    private static async Task ReadyAndWaitAsync()
    {
        Console.Out.WriteLine("READY");
        Console.Out.Flush();
        // Runs until killed, or until stdin closes for a graceful release.
        _ = await Console.In.ReadToEndAsync();
    }
}
