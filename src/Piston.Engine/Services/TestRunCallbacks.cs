// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>Per-project callbacks raised while a test run is in flight. All members are optional.</summary>
public sealed record TestRunCallbacks
{
    internal bool ReuseDiscovery { get; init; }

    /// <summary>
    /// Raised while a project waits for exclusive test resources held or queued for by other runs on the
    /// machine, each time its queue status changes.
    /// </summary>
    public Action<string, TestResourceWait>? OnProjectWaiting { get; init; }

    /// <summary>Raised when resources have been acquired, before waiting for a test-process slot.</summary>
    public Action<string>? OnProjectResourcesAcquired { get; init; }

    /// <summary>Raised when a project's test process starts (after any pool queueing).</summary>
    public Action<string>? OnProjectStarted { get; init; }

    /// <summary>Raised with a live snapshot of a single project's results so far.</summary>
    public Action<string, IReadOnlyList<TestSuite>>? OnProjectProgress { get; init; }

    /// <summary>Raised with a project's authoritative results when its test process exits.</summary>
    public Action<ProjectTestResult>? OnProjectCompleted { get; init; }
}
