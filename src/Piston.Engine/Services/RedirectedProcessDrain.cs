// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Piston.Engine.Services;

/// <summary>Bounds the wait for redirected output after a process exits.</summary>
internal static class RedirectedProcessDrain
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    internal static async Task<bool> WaitForExitAndDrainAsync(
        OwnedTestProcess ownedProcess,
        TimeSpan drainTimeout,
        CancellationToken ct,
        Action<string> onDrainTimeout,
        TimeSpan? exitTimeout = null)
    {
        var process = ownedProcess.Process;
        if (exitTimeout is { } timeout)
        {
            try
            {
                await WaitForProcessExitAsync(process, ct).WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                await KillAndWaitAsync(ownedProcess, ct, onDrainTimeout).ConfigureAwait(false);
                return false;
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
            await KillAndWaitAsync(ownedProcess, ct, onDrainTimeout).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            onDrainTimeout($"Redirected output drain exceeded {drainTimeout.TotalSeconds:g}s; output was cut off.");
            await KillAndWaitAsync(ownedProcess, ct, onDrainTimeout).ConfigureAwait(false);
            return false;
        }
    }

    internal static async Task KillAndWaitAsync(
        OwnedTestProcess ownedProcess,
        CancellationToken ct,
        Action<string> onTimeout)
    {
        var process = ownedProcess.Process;
        var killError = ownedProcess.KillProcessTree();
        if (killError is not null)
        {
            onTimeout($"Could not kill the process tree: {killError}");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(KillTimeout);
        try
        {
            await WaitForProcessExitAsync(process, timeout.Token).ConfigureAwait(false);
            // The host may have established ownership between the first kill and its exit.
            killError = ownedProcess.KillProcessTree();
            if (killError is not null)
            {
                onTimeout($"Could not kill the process tree: {killError}");
            }

            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await ownedProcess.WaitForTreeExitAsync(timeout.Token).ConfigureAwait(false);
            if (killError is null)
            {
                ownedProcess.CompleteCleanup();
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            onTimeout($"The process tree did not exit and drain within {KillTimeout.TotalSeconds:0}s after being killed; output was cut off.");
        }
        finally
        {
            StopReading(process);
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
                _ = exited.TrySetResult();
            }

            await exited.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            process.Exited -= OnExited;
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
