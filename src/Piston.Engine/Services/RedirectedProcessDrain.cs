// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.ComponentModel;
using System.Diagnostics;

namespace Piston.Engine.Services;

/// <summary>Bounds the wait for redirected output after a process exits.</summary>
internal static class RedirectedProcessDrain
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<bool> WaitForExitAndDrainAsync(
        Process process,
        TimeSpan drainTimeout,
        CancellationToken ct,
        Action<string> onDrainTimeout,
        TimeSpan? exitTimeout = null)
    {
        if (exitTimeout is { } timeout)
        {
            try
            {
                await WaitForProcessExitAsync(process, ct).WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                var killError = KillProcessTree(process);
                try
                {
                    await WaitForProcessExitAsync(process, ct).WaitAsync(KillTimeout, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    StopReading(process);
                    onDrainTimeout($"The process did not exit after being killed within {KillTimeout.TotalSeconds:0}s.");
                    return false;
                }

                if (killError is not null)
                {
                    onDrainTimeout($"Could not kill the process tree: {killError}");
                }
            }
        }
        else
        {
            await WaitForProcessExitAsync(process, ct).ConfigureAwait(false);
        }

        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(drainTimeout);
        try
        {
            await process.WaitForExitAsync(drainCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var killError = KillProcessTree(process);
            StopReading(process);
            var message = $"Redirected output drain exceeded {drainTimeout.TotalSeconds:0}s; output was cut off.";
            if (killError is not null)
            {
                message += $" Could not kill the process tree: {killError}";
            }

            onDrainTimeout(message);
            return false;
        }
    }

    internal static async Task KillAndWaitAsync(
        Process process,
        TimeSpan drainTimeout,
        CancellationToken ct,
        Action<string> onTimeout)
    {
        var killError = KillProcessTree(process);
        try
        {
            await WaitForProcessExitAsync(process, ct).WaitAsync(KillTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            StopReading(process);
            onTimeout($"The process did not exit after being killed within {KillTimeout.TotalSeconds:0}s.");
            return;
        }

        if (!await WaitForExitAndDrainAsync(process, drainTimeout, ct, onTimeout).ConfigureAwait(false))
        {
            return;
        }

        if (killError is not null)
        {
            onTimeout($"Could not kill the process tree: {killError}");
        }
    }

    internal static async Task WaitForProcessExitAsync(Process process, CancellationToken ct)
    {
        if (process.HasExited)
        {
            return;
        }

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnExited(object? _, EventArgs __) => exited.TrySetResult();

        process.Exited += OnExited;
        process.EnableRaisingEvents = true;
        try
        {
            if (process.HasExited)
            {
                exited.TrySetResult();
            }

            await exited.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            process.Exited -= OnExited;
        }
    }

    private static string? KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            return null;
        }
        catch (InvalidOperationException) when (process.HasExited)
        {
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return ex.Message;
        }
    }

    private static void StopReading(Process process)
    {
        try
        {
            process.CancelOutputRead();
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            process.CancelErrorRead();
        }
        catch (InvalidOperationException)
        {
        }
    }
}
