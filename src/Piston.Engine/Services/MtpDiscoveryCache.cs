// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;

namespace Piston.Engine.Services;

/// <summary>Reuses successful server discoveries while the test app's build is unchanged.</summary>
internal sealed class MtpDiscoveryCache
{
    internal sealed record Discovery(MtpServerSession.Result Result, IReadOnlyList<JsonElement> Nodes);
    internal sealed record Lookup(Discovery Discovery, bool Cached);

    private readonly ConcurrentDictionary<(string AppPath, string WorkingDirectory), Entry> _entries = new();
    private long _generation;

    /// <summary>Forgets discoveries before a build, even if its output timestamps do not change.</summary>
    public void Invalidate() => Interlocked.Increment(ref _generation);

    public async Task<Lookup> GetAsync(
        string appPath,
        string workingDirectory,
        Func<CancellationToken, Task<Discovery>> discover,
        CancellationToken ct)
    {
        var key = (Path.GetFullPath(appPath), Path.GetFullPath(workingDirectory));
        var entry = _entries.GetOrAdd(key, static _ => new Entry());
        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var generation = Interlocked.Read(ref _generation);
            var fingerprint = MtpWarmHostPool.OutputFingerprint(key.Item1);
            if (entry.Value is not null && entry.Generation == generation && entry.Fingerprint == fingerprint)
            {
                return new Lookup(entry.Value, Cached: true);
            }

            var found = await discover(ct).ConfigureAwait(false);
            if (found.Result is { Outcome: MtpServerSession.Outcome.Completed, Error: null }
                && !ct.IsCancellationRequested
                && generation == Interlocked.Read(ref _generation)
                && fingerprint == MtpWarmHostPool.OutputFingerprint(key.Item1))
            {
                entry.Generation = generation;
                entry.Fingerprint = fingerprint;
                entry.Value = found with { Nodes = [.. found.Nodes.Select(node => node.Clone())] };
            }
            else
            {
                entry.Value = null;
            }

            return new Lookup(found, Cached: false);
        }
        finally
        {
            _ = entry.Gate.Release();
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public long Generation { get; set; }
        public string? Fingerprint { get; set; }
        public Discovery? Value { get; set; }
    }
}
