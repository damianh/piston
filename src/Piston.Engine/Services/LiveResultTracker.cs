// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Live per-test results of one <c>dotnet test</c> run, keyed by <see cref="TestIdentity"/> so they line up
/// with seeded and final (TRX) results. Collector events carry the identity; console result lines only carry
/// the display name, which is mapped to the identity the collector reported for it. Display names need not
/// be unique (the same method name in two classes), so an ambiguous console line is left to the collector.
/// </summary>
internal sealed class LiveResultTracker
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TestStatus> _results = new(StringComparer.Ordinal);

    // Display name -> identity reported by the collector; null when several tests share the display name.
    private readonly Dictionary<string, string?> _identityByDisplayName = new(StringComparer.Ordinal);

    /// <summary>Applies a collector event; returns <see langword="true"/> when the results changed.</summary>
    public bool ApplyCollectorEvent(LiveTestEvent evt)
    {
        var identity = evt.Identity;
        lock (_lock)
        {
            if (!_identityByDisplayName.TryGetValue(evt.DisplayName, out var known))
            {
                _identityByDisplayName[evt.DisplayName] = identity;

                // A console line that arrived before the collector named its test is keyed by display name.
                if (identity != evt.DisplayName && _results.Remove(evt.DisplayName, out var early))
                {
                    _results[identity] = early;
                }
            }
            else if (known is not null && known != identity)
            {
                _identityByDisplayName[evt.DisplayName] = null;
            }

            var exists = _results.TryGetValue(identity, out var current);
            // Console result lines are authoritative; the collector only fills gaps.
            if (evt.Status == TestStatus.Running ? exists : exists && current != TestStatus.Running)
            {
                return false;
            }

            _results[identity] = evt.Status;
            return true;
        }
    }

    /// <summary>Applies a console result line; returns <see langword="true"/> when the results changed.</summary>
    public bool ApplyConsoleResult(string displayName, TestStatus status)
    {
        lock (_lock)
        {
            string key;
            if (_identityByDisplayName.TryGetValue(displayName, out var identity))
            {
                if (identity is null)
                {
                    return false;
                }

                key = identity;
            }
            else
            {
                // No collector event for it (yet, or no collector at all): the display name is all there is.
                key = displayName;
            }

            _results[key] = status;
            return true;
        }
    }

    /// <summary>The live results so far, as a single suite.</summary>
    public IReadOnlyList<TestSuite> Snapshot()
    {
        List<KeyValuePair<string, TestStatus>> snapshot;
        lock (_lock)
        {
            snapshot = [.. _results];
        }

        if (snapshot.Count == 0)
        {
            return [];
        }

        var results = snapshot
            .Select(kvp => new TestResult(
                FullyQualifiedName: kvp.Key,
                DisplayName: TestProcessRunner.DisplayNameOf(kvp.Key),
                Status: kvp.Value,
                Duration: TimeSpan.Zero,
                Output: null,
                ErrorMessage: null,
                StackTrace: null,
                Source: null))
            .ToList();

        return [new TestSuite("…", results, DateTimeOffset.UtcNow, TimeSpan.Zero)];
    }
}
