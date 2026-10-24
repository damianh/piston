// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol;
using Piston.Protocol.Messages;

namespace Piston.Hosting;

internal static class StateSnapshotWaiter
{
    public static async Task<StateSnapshotNotification?> WaitForSnapshotAsync(
        IEngineClient client,
        TimeSpan timeout)
    {
        if (client.CurrentSnapshot is { } currentSnapshot)
        {
            return currentSnapshot;
        }

        var snapshotReceived = new TaskCompletionSource<StateSnapshotNotification>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStateChanged(StateSnapshotNotification snapshot) => snapshotReceived.TrySetResult(snapshot);

        client.StateChanged += OnStateChanged;
        try
        {
            if (client.CurrentSnapshot is { } snapshot)
            {
                return snapshot;
            }

            try
            {
                return await snapshotReceived.Task.WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return client.CurrentSnapshot;
            }
        }
        finally
        {
            client.StateChanged -= OnStateChanged;
        }
    }
}
