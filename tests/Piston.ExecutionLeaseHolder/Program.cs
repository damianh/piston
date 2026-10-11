// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;

namespace Piston.ExecutionLeaseHolder;

/// <summary>
/// Holds worktree ownership or an execution admission in a separate process so tests can verify
/// cross-process exclusion and recovery after the process is killed.
/// <list type="bullet">
/// <item><c>own &lt;dir&gt; &lt;solution&gt;</c>: acquire ownership, print <c>READY</c>, wait.</item>
/// <item><c>admit &lt;dir&gt; &lt;cpu&gt; &lt;memoryMb|0&gt;</c>: print <c>QUEUED n</c> while queued, <c>READY</c> once admitted, wait.</item>
/// <item><c>probe &lt;dir&gt;</c>: prepare the directory and exit 0, or print the error and exit 2.</item>
/// </list>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
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
