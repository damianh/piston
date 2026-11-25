// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>Reads live worktree ownership and prunes stale diagnostic sidecars.</summary>
public static class MachineExecutionOverviewReader
{
    public static Task<MachineExecutionOverview> ReadAsync(
        ExecutionCoordinationDirectory directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.UtcNow;
        try
        {
            var owners = WorktreeOwnership.ListOwners(directory, reportErrors: true);
            return Task.FromResult(new MachineExecutionOverview(capturedAt, owners, null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new MachineExecutionOverview(
                capturedAt, [], $"Could not read worktree ownership: {ex.Message}", OwnersAvailable: false));
        }
    }
}
