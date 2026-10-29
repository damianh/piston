// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Piston.Engine.Models;

namespace Piston.Engine.Execution;

/// <summary>
/// Machine-wide FIFO scheduling of test project runs over exclusive resources, coordinated through files
/// in an <see cref="ExecutionCoordinationDirectory"/> like <see cref="MachineExecutionCoordinator"/>:
/// <list type="bullet">
/// <item><c>resources.lock</c> serializes evaluation across processes (held only briefly).</item>
/// <item>A waiting run holds an exclusively locked <c>resource-queue/*.ticket</c>, a running one a
/// <c>resource-leases/*.lease</c>. Each has a <c>.json</c> sidecar naming its resources, process, worktree
/// and project, since a locked file cannot be read by other processes.</item>
/// <item>Entries whose lock is not held belong to exited processes and are removed with their sidecars on
/// every evaluation.</item>
/// </list>
/// A run starts when no running project holds any of its resources, it fits the machine's
/// <c>containerSlots</c>, and no earlier queued run conflicts with it, so a run is never starved. Runs take
/// all their resources at once, so they cannot deadlock.
/// </summary>
internal sealed class MachineResourceCoordinator : ITestResourceGate
{
    private const string TicketExtension = ".ticket";
    private const string LeaseExtension = ".lease";
    private const string InfoExtension = ".json";

    private readonly ExecutionCoordinationDirectory _directory;
    private readonly Func<string, TestResourceNeeds> _resolve;
    private readonly string? _worktree;
    private readonly TimeSpan _pollInterval;
    private readonly Func<int, bool> _isPortInUse;

    public MachineResourceCoordinator(
        ExecutionCoordinationDirectory directory,
        Func<string, TestResourceNeeds> resolve,
        string? worktree,
        TimeSpan? pollInterval = null,
        Func<int, bool>? isPortInUse = null)
    {
        _directory = directory;
        _resolve = resolve;
        _worktree = worktree;
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        _isPortInUse = isPortInUse ?? IsPortInUse;
    }

    public TestResourceNeeds Resolve(string projectPath) => _resolve(projectPath);

    public async Task<TestResourceLease> AcquireAsync(
        TestResourceNeeds needs, Action<TestResourceWait> onWaiting, CancellationToken ct)
    {
        if (needs.IsEmpty)
        {
            return TestResourceLease.Uncoordinated(needs);
        }

        var requestedAt = DateTimeOffset.UtcNow;
        var info = new EntryInfo(needs.Keys, needs.UsesContainers, Environment.ProcessId, _worktree, needs.ProjectPath, requestedAt, null);
        var id = Guid.NewGuid().ToString("N");

        Held ticket;
        long sequence;
        using (await AcquireLockAsync(ct).ConfigureAwait(false))
        {
            var (tickets, leases) = Scan();
            sequence = tickets.Concat(leases).Select(e => e.Sequence).DefaultIfEmpty(0).Max() + 1;
            sequence = Math.Max(DateTime.UtcNow.Ticks, sequence);
            ticket = CreateEntry(_directory.ResourceQueueDirectory, Stem(sequence, id), TicketExtension, info);
        }

        TestResourceWait? lastPublished = null;
        Held? unreturnedLease = null;
        try
        {
            while (true)
            {
                TestResourceWait status;
                using (await AcquireLockAsync(ct).ConfigureAwait(false))
                {
                    var budget = ExecutionBudget.Load(_directory.BudgetPath);
                    var (tickets, leases) = Scan();
                    status = Evaluate(needs, sequence, ticket.Name, tickets, leases, budget, requestedAt);

                    if (status.State == TestResourceWaitState.Acquired)
                    {
                        var leaseInfo = info with { AcquiredAt = status.AcquiredAt };
                        unreturnedLease = CreateEntry(_directory.ResourceLeasesDirectory, Path.GetFileNameWithoutExtension(ticket.Name), LeaseExtension, leaseInfo);
                        Release(ticket, lockHeld: true);
                    }
                }

                if (status.State == TestResourceWaitState.Acquired)
                {
                    var lease = unreturnedLease!;
                    var warnings = ProbePorts(needs);
                    unreturnedLease = null;
                    return new TestResourceLease(needs, status, warnings, () => Release(lease, lockHeld: false));
                }

                if (!status.SameAs(lastPublished))
                {
                    lastPublished = status;
                    onWaiting(status);
                }

                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                if (unreturnedLease is not null)
                {
                    Release(unreturnedLease, lockHeld: false);
                }
            }
            finally
            {
                Release(ticket, lockHeld: false);
            }
            throw;
        }
    }

    private static TestResourceWait Evaluate(
        TestResourceNeeds needs, long sequence, string name, IReadOnlyList<Entry> tickets, IReadOnlyList<Entry> leases,
        ExecutionBudget budget, DateTimeOffset requestedAt)
    {
        var keys = needs.Keys.ToHashSet(StringComparer.Ordinal);
        var blockers = new List<TestResourceBlocker>();

        foreach (var lease in leases)
        {
            blockers.AddRange(lease.Info.Keys.Where(keys.Contains)
                .Select(k => Blocker(k, TestResourceBlockerKind.Held, lease)));
        }

        var earlier = tickets.Where(t => t.CompareTo(sequence, name) < 0).ToList();
        var ahead = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ticket in earlier)
        {
            foreach (var key in ticket.Info.Keys.Where(keys.Contains))
            {
                blockers.Add(Blocker(key, TestResourceBlockerKind.Queued, ticket));
                _ = ahead.Add(ticket.Name);
            }
        }

        var containersInUse = leases.Count(l => l.Info.UsesContainers);
        if (needs.UsesContainers && budget.ContainerSlots is { } limit)
        {
            var earlierContainers = earlier.Where(t => t.Info.UsesContainers).ToList();
            if (containersInUse + earlierContainers.Count >= limit)
            {
                blockers.AddRange(leases.Where(l => l.Info.UsesContainers)
                    .Select(l => Blocker(TestResourceBlocker.ContainerSlots, TestResourceBlockerKind.Held, l)));
                foreach (var ticket in earlierContainers)
                {
                    blockers.Add(Blocker(TestResourceBlocker.ContainerSlots, TestResourceBlockerKind.Queued, ticket));
                    _ = ahead.Add(ticket.Name);
                }
            }
        }

        var acquired = blockers.Count == 0;
        return new TestResourceWait(
            acquired ? TestResourceWaitState.Acquired : TestResourceWaitState.Waiting,
            needs.Keys,
            needs.UsesContainers,
            [.. blockers.OrderBy(b => b.Kind).ThenBy(b => b.Since).ThenBy(b => b.Resource, StringComparer.Ordinal)],
            ahead.Count,
            containersInUse + (acquired && needs.UsesContainers ? 1 : 0),
            budget.ContainerSlots,
            requestedAt,
            acquired ? DateTimeOffset.UtcNow : null);
    }

    private static TestResourceBlocker Blocker(string resource, TestResourceBlockerKind kind, Entry entry) =>
        new(resource, kind, entry.Info.ProcessId, entry.Info.Worktree, entry.Info.Project,
            entry.Info.AcquiredAt ?? entry.Info.RequestedAt);

    private IReadOnlyList<string> ProbePorts(TestResourceNeeds needs)
    {
        var warnings = new List<string>();
        foreach (var port in needs.Keys.Select(TestResourceKey.TryGetPort).OfType<int>())
        {
            if (_isPortInUse(port))
            {
                warnings.Add(
                    $"Port {port} is already in use by a process outside Piston's test resource scheduling; " +
                    "tests that bind it may fail.");
            }
        }

        return warnings;
    }

    /// <summary>Whether a TCP listener on <paramref name="port"/> already exists on any local address.</summary>
    internal static bool IsPortInUse(int port)
    {
        IPAddress[] addresses = Socket.OSSupportsIPv6
            ? [IPAddress.Any, IPAddress.Loopback, IPAddress.IPv6Any, IPAddress.IPv6Loopback]
            : [IPAddress.Any, IPAddress.Loopback];
        foreach (var address in addresses)
        {
            try
            {
                var listener = new TcpListener(address, port);
                listener.Start();
                listener.Stop();
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                return true;
            }
            catch (SocketException)
            {
                // The address family or address is unavailable on this machine.
            }
        }

        return false;
    }

    private async Task<FileStream> AcquireLockAsync(CancellationToken ct)
    {
        try
        {
            return await ExclusiveFileLock.AcquireAsync(_directory.ResourceLockPath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot acquire the test resource lock '{_directory.ResourceLockPath}': {ex.Message}", ex);
        }
    }

    private static string Stem(long sequence, string id) =>
        string.Join('_',
            sequence.ToString("D20", CultureInfo.InvariantCulture),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            id);

    /// <summary>Must be called under the resource lock. Writes the sidecar, then creates and locks the entry.</summary>
    private static Held CreateEntry(string directory, string stem, string extension, EntryInfo info)
    {
        var path = Path.Combine(directory, stem + extension);
        var infoPath = Path.Combine(directory, stem + InfoExtension);
        try
        {
            File.WriteAllBytes(infoPath, info.ToJson());
            var options = OperatingSystem.IsWindows() ? FileOptions.None : FileOptions.DeleteOnClose;
            var stream = ExclusiveFileLock.TryAcquire(path, FileMode.CreateNew, options)
                ?? throw new ExecutionCoordinationException($"Test resource entry '{path}' is unexpectedly locked.");
            return new Held(stream, path, infoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Delete(infoPath);
            throw new ExecutionCoordinationException($"Cannot create test resource entry '{path}': {ex.Message}", ex);
        }
    }

    private void Release(Held entry, bool lockHeld)
    {
        try
        {
            // Under the lock, so no evaluator sees a live entry without its sidecar.
            using var resourceLock = lockHeld
                ? null
                : ExclusiveFileLock.AcquireAsync(_directory.ResourceLockPath, CancellationToken.None).GetAwaiter().GetResult();
            entry.Stream.Dispose();
            if (OperatingSystem.IsWindows())
            {
                File.Delete(entry.Path);
            }

            File.Delete(entry.InfoPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            entry.Stream.Dispose();
            throw new ExecutionCoordinationException($"Cannot release test resource entry '{entry.Path}': {ex.Message}", ex);
        }
    }

    /// <summary>Must be called under the resource lock. Removes entries and sidecars of exited processes.</summary>
    private (IReadOnlyList<Entry> Tickets, IReadOnlyList<Entry> Leases) Scan() =>
        (ScanLive(_directory.ResourceQueueDirectory, TicketExtension), ScanLive(_directory.ResourceLeasesDirectory, LeaseExtension));

    private static List<Entry> ScanLive(string directory, string extension)
    {
        var live = new List<Entry>();
        try
        {
            var files = System.IO.Directory.GetFiles(directory);
            var liveStems = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in files.Where(f => f.EndsWith(extension, StringComparison.Ordinal)))
            {
                var stem = Path.GetFileNameWithoutExtension(path);
                var infoPath = Path.Combine(directory, stem + InfoExtension);
                if (ExclusiveFileLock.TryRemoveUnheld(path))
                {
                    File.Delete(infoPath);
                    continue;
                }

                _ = liveStems.Add(stem);
                if (!Entry.TryParse(Path.GetFileName(path), out var sequence)
                    || EntryInfo.TryRead(infoPath) is not { } info)
                {
                    throw new ExecutionCoordinationException(
                        $"Cannot interpret held test resource entry '{path}' or its sidecar '{infoPath}'. Resources cannot safely be granted.");
                }

                live.Add(new Entry(sequence, Path.GetFileName(path), info));
            }

            // Sidecars of entries whose process died between writing the sidecar and its entry, or after.
            foreach (var path in files.Where(f => f.EndsWith(InfoExtension, StringComparison.Ordinal)))
            {
                if (!liveStems.Contains(Path.GetFileNameWithoutExtension(path)))
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException($"Cannot read test resource queue '{directory}': {ex.Message}", ex);
        }

        return live;
    }

    private sealed record Held(FileStream Stream, string Path, string InfoPath)
    {
        public string Name => System.IO.Path.GetFileName(Path);
    }

    private sealed record Entry(long Sequence, string Name, EntryInfo Info)
    {
        public int CompareTo(long sequence, string name)
        {
            var bySequence = Sequence.CompareTo(sequence);
            return bySequence != 0 ? bySequence : string.CompareOrdinal(Name, name);
        }

        public static bool TryParse(string fileName, out long sequence)
        {
            sequence = 0;
            var parts = Path.GetFileNameWithoutExtension(fileName).Split('_');
            return parts.Length == 3
                && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out sequence)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out _)
                && parts[2].Length > 0;
        }
    }

    private sealed record EntryInfo(
        IReadOnlyList<string> Keys,
        bool UsesContainers,
        int ProcessId,
        string? Worktree,
        string? Project,
        DateTimeOffset RequestedAt,
        DateTimeOffset? AcquiredAt)
    {
        public byte[] ToJson()
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteStartArray("keys");
                foreach (var key in Keys)
                {
                    writer.WriteStringValue(key);
                }

                writer.WriteEndArray();
                writer.WriteBoolean("usesContainers", UsesContainers);
                writer.WriteNumber("processId", ProcessId);
                writer.WriteString("worktree", Worktree);
                writer.WriteString("project", Project);
                writer.WriteString("requestedAt", RequestedAt);
                if (AcquiredAt is { } acquiredAt)
                {
                    writer.WriteString("acquiredAt", acquiredAt);
                }

                writer.WriteEndObject();
            }

            return buffer.ToArray();
        }

        public static EntryInfo? TryRead(string path)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var root = document.RootElement;
                var keys = root.GetProperty("keys").EnumerateArray().Select(k => k.GetString()).ToList();
                if (keys.Any(string.IsNullOrWhiteSpace))
                {
                    return null;
                }

                return new EntryInfo(
                    [.. keys.Select(k => k!)],
                    root.GetProperty("usesContainers").GetBoolean(),
                    root.GetProperty("processId").GetInt32(),
                    root.TryGetProperty("worktree", out var worktree) ? worktree.GetString() : null,
                    root.TryGetProperty("project", out var project) ? project.GetString() : null,
                    root.GetProperty("requestedAt").GetDateTimeOffset(),
                    root.TryGetProperty("acquiredAt", out var acquiredAt) ? acquiredAt.GetDateTimeOffset() : null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                                        or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                return null;
            }
        }
    }
}
