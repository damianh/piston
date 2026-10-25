// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>A machine-wide read-only snapshot of worktree owners, running reservations and queued runs.</summary>
public sealed record MachineExecutionOverview(
    DateTimeOffset CapturedAt,
    ExecutionBudget? Budget,
    IReadOnlyList<OwnershipRecord> Owners,
    IReadOnlyList<MachineExecutionEntry> Running,
    IReadOnlyList<MachineExecutionEntry> Queue,
    string? Error,
    bool CoordinationAvailable = true)
{
    public int? CpuSlotsInUse => CoordinationAvailable ? Running.Sum(entry => entry.CpuSlots) : null;
    public long? MemoryMbInUse => CoordinationAvailable ? Running.Sum(entry => (long)(entry.MemoryMb ?? 0)) : null;
}

/// <summary>One running or queued machine-wide execution reservation.</summary>
public sealed record MachineExecutionEntry(
    long Sequence,
    string RunId,
    int ProcessId,
    int CpuSlots,
    int? MemoryMb,
    string? WorktreeKey,
    int? QueuePosition);

/// <summary>Reads the filesystem-backed machine coordination state without changing it.</summary>
public static class MachineExecutionOverviewReader
{
    private const string TicketExtension = ".ticket";
    private const string LeaseExtension = ".lease";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(500);

    public static async Task<MachineExecutionOverview> ReadAsync(
        ExecutionCoordinationDirectory directory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directory);
        var capturedAt = DateTimeOffset.UtcNow;
        var owners = WorktreeOwnership.ListOwners(directory);
        var errors = new List<string>();

        ExecutionBudget? budget = null;
        try
        {
            budget = ExecutionBudget.Load(directory.BudgetPath);
        }
        catch (ExecutionCoordinationException ex)
        {
            errors.Add(ex.Message);
        }

        var tickets = new List<ExecutionQueueEntry>();
        var leases = new List<ExecutionQueueEntry>();
        var coordinationAvailable = true;
        if (Directory.Exists(directory.StateDirectory))
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(LockTimeout);
                using var admissionLock = await ExclusiveFileLock
                    .AcquireAsync(directory.AdmissionLockPath, timeout.Token)
                    .ConfigureAwait(false);
                ReadEntries(directory.QueueDirectory, TicketExtension, tickets);
                ReadEntries(directory.LeasesDirectory, LeaseExtension, leases);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                coordinationAvailable = false;
                errors.Add("Timed out waiting for the machine admission lock; running and queued work are unknown.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExecutionCoordinationException)
            {
                coordinationAvailable = false;
                errors.Add($"Could not read the machine execution queue: {ex.Message}");
            }
        }

        if (!coordinationAvailable)
        {
            tickets.Clear();
            leases.Clear();
        }

        var liveOwnersByProcess = owners
            .Where(owner => owner.Liveness == OwnershipLiveness.Live && owner.Owner is not null)
            .GroupBy(owner => owner.Owner!.ProcessId)
            .ToDictionary(group => group.Key, group => group.First());

        var running = leases
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.FileName, StringComparer.Ordinal)
            .Select(entry => ToOverviewEntry(entry, liveOwnersByProcess, queuePosition: null))
            .ToList();
        var queue = tickets
            .OrderBy(entry => entry.Sequence)
            .ThenBy(entry => entry.FileName, StringComparer.Ordinal)
            .Select((entry, index) => ToOverviewEntry(entry, liveOwnersByProcess, index + 1))
            .ToList();

        return new MachineExecutionOverview(
            capturedAt, budget, owners, running, queue,
            errors.Count == 0 ? null : string.Join(Environment.NewLine, errors), coordinationAvailable);
    }

    private static MachineExecutionEntry ToOverviewEntry(
        ExecutionQueueEntry entry, IReadOnlyDictionary<int, OwnershipRecord> owners, int? queuePosition) =>
        new(
            entry.Sequence,
            entry.RunId,
            entry.ProcessId,
            entry.CpuSlots,
            entry.MemoryMb,
            owners.TryGetValue(entry.ProcessId, out var owner) ? owner.Key : null,
            queuePosition);

    private static void ReadEntries(string directory, string extension, ICollection<ExecutionQueueEntry> entries)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(path);
            FileStream? probe;
            try
            {
                probe = ExclusiveFileLock.TryAcquire(path, FileMode.Open);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }

            if (probe is not null)
            {
                probe.Dispose();
                continue;
            }

            if (!fileName.EndsWith(extension, StringComparison.Ordinal)
                || !ExecutionQueueEntry.TryParse(fileName, out var entry))
            {
                throw new ExecutionCoordinationException(
                    $"Unrecognised held entry '{path}' in the execution coordination directory.");
            }

            entries.Add(entry);
        }
    }
}
