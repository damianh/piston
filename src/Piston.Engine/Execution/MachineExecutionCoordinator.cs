// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using Piston.Engine.Models;

namespace Piston.Engine.Execution;

/// <summary>
/// Machine-wide FIFO admission of build/test runs against an <see cref="ExecutionBudget"/>, coordinated
/// through files in an <see cref="ExecutionCoordinationDirectory"/>:
/// <list type="bullet">
/// <item><c>admission.lock</c> serializes queue evaluation across processes (held only briefly).</item>
/// <item>A queued run holds an exclusively locked <c>queue/*.ticket</c>; an admitted run holds a
/// <c>leases/*.lease</c>. File names encode the queue sequence and reservation.</item>
/// <item>Files whose lock is not held belong to exited processes and are removed on every evaluation,
/// which recovers slots and queue positions after crashes without timeouts or PID checks.</item>
/// </list>
/// Admission is strict FIFO: a run is admitted only at the head of the queue and only when its
/// reservation fits next to the reservations of already admitted runs, so large runs are not starved.
/// </summary>
internal sealed class MachineExecutionCoordinator : IExecutionAdmission
{
    private const string TicketExtension = ".ticket";
    private const string LeaseExtension = ".lease";

    private readonly ExecutionCoordinationDirectory _directory;
    private readonly ExecutionReservation _reservation;
    private readonly TimeSpan _pollInterval;
    private volatile bool _initialized;

    public MachineExecutionCoordinator(
        ExecutionCoordinationDirectory directory, ExecutionReservation reservation, TimeSpan? pollInterval = null)
    {
        _directory = directory;
        _reservation = reservation;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
    }

    public ExecutionCoordinationDirectory Directory => _directory;
    public ExecutionReservation Reservation => _reservation;

    /// <summary>Prepares the directory and validates the budget; throws when coordination is unusable.</summary>
    public ExecutionBudget Initialize()
    {
        _directory.EnsureReady();
        var budget = LoadValidatedBudget();
        _initialized = true;
        return budget;
    }

    public async Task<IExecutionLease> AdmitAsync(
        string runId, Action<TestRunAdmission> onStatus, long? inheritedSequence, CancellationToken ct)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException($"{nameof(MachineExecutionCoordinator)} must be initialized before admitting runs.");
        }

        if (runId.Contains('_') || runId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException($"Run id '{runId}' cannot be encoded in a queue entry name.", nameof(runId));
        }

        long sequence;
        FileStream ticket;
        string ticketName;
        try
        {
            using (await AcquireGlobalLockAsync(ct).ConfigureAwait(false))
            {
                _ = LoadValidatedBudget();
                var (tickets, leases) = ScanLiveEntries();
                sequence = inheritedSequence ?? NextSequence(tickets, leases);
                ticketName = EntryName(sequence, runId) + TicketExtension;
                ticket = CreateEntry(Path.Combine(_directory.QueueDirectory, ticketName));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new ExecutionAdmissionCancelledException(inheritedSequence, ct);
        }

        var queuedAt = new DateTimeOffset(Math.Clamp(sequence, 0, DateTime.MaxValue.Ticks), TimeSpan.Zero);
        TestRunAdmission? lastPublished = null;
        FileStream? unreturnedLease = null;
        try
        {
            while (true)
            {
                TestRunAdmission status;
                using (await AcquireGlobalLockAsync(ct).ConfigureAwait(false))
                {
                    var budget = LoadValidatedBudget();
                    var (tickets, leases) = ScanLiveEntries();

                    var ahead = tickets.Count(t => t.CompareTo(sequence, ticketName) < 0);
                    var cpuInUse = leases.Sum(l => l.CpuSlots);
                    var memoryInUse = leases.Sum(l => (long)(l.MemoryMb ?? 0));
                    var fits = (long)cpuInUse + _reservation.CpuSlots <= budget.CpuSlots
                        && (budget.MemoryMb is not { } memoryBudget || memoryInUse + (_reservation.MemoryMb ?? 0) <= memoryBudget);

                    if (ahead == 0 && fits)
                    {
                        var leasePath = Path.Combine(_directory.LeasesDirectory, EntryName(sequence, runId) + LeaseExtension);
                        unreturnedLease = CreateEntry(leasePath);
                        ReleaseEntry(ticket, _directory.AdmissionLockPath, lockHeld: true);
                        var admitted = Snapshot(TestRunAdmissionState.Admitted, budget, 0, tickets.Count - 1,
                            leases.Count, cpuInUse, memoryInUse, queuedAt, DateTimeOffset.UtcNow);
                        var lease = new Lease(unreturnedLease, admitted, _directory.AdmissionLockPath);
                        unreturnedLease = null;
                        return lease;
                    }

                    status = Snapshot(TestRunAdmissionState.Queued, budget, ahead, tickets.Count,
                        leases.Count, cpuInUse, memoryInUse, queuedAt, admittedAt: null);
                }

                if (status != lastPublished)
                {
                    lastPublished = status;
                    onStatus(status);
                }

                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ReleaseEntry(ticket, _directory.AdmissionLockPath);
            throw new ExecutionAdmissionCancelledException(sequence, ct);
        }
        catch
        {
            try
            {
                if (unreturnedLease is not null)
                {
                    ReleaseEntry(unreturnedLease, _directory.AdmissionLockPath);
                }
            }
            finally
            {
                ReleaseEntry(ticket, _directory.AdmissionLockPath);
            }
            throw;
        }
    }

    private TestRunAdmission Snapshot(
        TestRunAdmissionState state, ExecutionBudget budget, int ahead, int queueLength,
        int otherAdmitted, int cpuInUse, long memoryInUse, DateTimeOffset queuedAt, DateTimeOffset? admittedAt) =>
        new(state, _reservation.CpuSlots, _reservation.MemoryMb, budget.CpuSlots, budget.MemoryMb, budget.Source,
            ahead, queueLength, otherAdmitted, cpuInUse, memoryInUse, queuedAt, admittedAt);

    private ExecutionBudget LoadValidatedBudget()
    {
        var budget = ExecutionBudget.Load(_directory.BudgetPath);
        _reservation.EnsureAdmissible(budget);
        return budget;
    }

    private async Task<FileStream> AcquireGlobalLockAsync(CancellationToken ct)
    {
        try
        {
            return await ExclusiveFileLock.AcquireAsync(_directory.AdmissionLockPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot acquire the machine admission lock '{_directory.AdmissionLockPath}': {ex.Message}", ex);
        }
    }

    private string EntryName(long sequence, string runId) =>
        string.Join('_',
            sequence.ToString("D20", CultureInfo.InvariantCulture),
            _reservation.CpuSlots.ToString(CultureInfo.InvariantCulture),
            (_reservation.MemoryMb ?? 0).ToString(CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            runId);

    private static FileStream CreateEntry(string path)
    {
        try
        {
            // Created under the admission lock, so no evaluator can observe it before it is locked.
            // Windows delete-pending files can report access denied rather than sharing violation.
            // Keep entries reopenable for liveness checks; remove them explicitly after releasing.
            var options = OperatingSystem.IsWindows() ? FileOptions.None : FileOptions.DeleteOnClose;
            return ExclusiveFileLock.TryAcquire(path, FileMode.CreateNew, options)
                ?? throw new ExecutionCoordinationException($"Queue entry '{path}' is unexpectedly locked.");
        }
        catch (IOException ex)
        {
            throw new ExecutionCoordinationException($"Cannot create queue entry '{path}': {ex.Message}", ex);
        }
    }

    private static long NextSequence(IReadOnlyList<ExecutionQueueEntry> tickets, IReadOnlyList<ExecutionQueueEntry> leases)
    {
        var maxLive = tickets.Concat(leases).Select(e => e.Sequence).DefaultIfEmpty(0).Max();
        return Math.Max(DateTime.UtcNow.Ticks, maxLive + 1);
    }

    /// <summary>Must be called under the admission lock. Removes entries of exited processes.</summary>
    private (IReadOnlyList<ExecutionQueueEntry> Tickets, IReadOnlyList<ExecutionQueueEntry> Leases) ScanLiveEntries() =>
        (ScanLive(_directory.QueueDirectory, TicketExtension), ScanLive(_directory.LeasesDirectory, LeaseExtension));

    private static List<ExecutionQueueEntry> ScanLive(string directory, string extension)
    {
        var live = new List<ExecutionQueueEntry>();
        try
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(directory))
            {
                if (ExclusiveFileLock.TryRemoveUnheld(path))
                {
                    continue;
                }

                var name = Path.GetFileName(path);
                if (!name.EndsWith(extension, StringComparison.Ordinal)
                    || !ExecutionQueueEntry.TryParse(name, out var entry))
                {
                    throw new ExecutionCoordinationException(
                        $"Unrecognised held entry '{path}' in the execution coordination directory; another " +
                        "Piston version or process may be using it. Use a separate PISTON_EXECUTION_DIRECTORY.");
                }

                live.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException($"Cannot read execution queue '{directory}': {ex.Message}", ex);
        }
        return live;
    }

    private sealed class Lease(FileStream file, TestRunAdmission status, string admissionLockPath) : IExecutionLease
    {
        private int _disposed;
        public TestRunAdmission? Status { get; } = status;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                ReleaseEntry(file, admissionLockPath);
            }
        }
    }

    private static void ReleaseEntry(FileStream file, string admissionLockPath, bool lockHeld = false)
    {
        var path = file.Name;
        try
        {
            // Serialize close-and-delete with scanners on Windows, where an open handle can deny deletion.
            using var globalLock = OperatingSystem.IsWindows() && !lockHeld
                ? ExclusiveFileLock.AcquireAsync(admissionLockPath, CancellationToken.None).GetAwaiter().GetResult()
                : null;
            file.Dispose();
            if (OperatingSystem.IsWindows())
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            file.Dispose();
            throw new ExecutionCoordinationException($"Cannot release execution entry '{path}': {ex.Message}", ex);
        }
    }
}
