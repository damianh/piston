// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;

namespace Piston.Engine.Services;

internal static class ProcessTermination
{
    internal static readonly TimeSpan WaitAfterKillTimeout = TimeSpan.FromSeconds(5);

    internal static Task KillAndWaitAsync(Process process, Action<string> log) =>
        KillAndWaitAsync(process, WaitAfterKillTimeout, log);

    internal static async Task KillAndWaitAsync(Process process, TimeSpan timeout, Action<string> log)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
        catch (Exception ex) when (ex is Win32Exception or AggregateException)
        {
            log($"Could not kill process tree after cancellation: {ex.Message}");
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log($"Process did not exit or finish draining redirected output within {timeout.TotalSeconds:g}s after cancellation.");
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
        }
    }
}
