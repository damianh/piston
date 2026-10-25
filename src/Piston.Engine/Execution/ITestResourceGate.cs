// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Execution;

/// <summary>
/// Serializes test project runs that share exclusive resources (ports, container names, a project isolated
/// per repository) or container slots, across all Piston daemons of the user on the machine.
/// </summary>
internal interface ITestResourceGate
{
    /// <summary>What <paramref name="projectPath"/>'s run needs.</summary>
    TestResourceNeeds Resolve(string projectPath);

    /// <summary>
    /// Waits until the run may hold <paramref name="needs"/>, reporting each change of its queue status
    /// while it waits. Dispose the lease when the run ends. Needs that are empty return immediately.
    /// </summary>
    Task<TestResourceLease> AcquireAsync(
        TestResourceNeeds needs, Action<TestResourceWait> onWaiting, CancellationToken ct);
}

/// <summary>The resources held for one test project run; disposing releases them.</summary>
internal sealed class TestResourceLease : IDisposable
{
    private readonly Action? _release;
    private int _disposed;

    internal TestResourceLease(
        TestResourceNeeds needs, TestResourceWait? status, IReadOnlyList<string> warnings, Action? release)
    {
        Needs = needs;
        Status = status;
        Warnings = warnings;
        _release = release;
    }

    public TestResourceNeeds Needs { get; }

    /// <summary>The acquired status, or null when nothing needed coordinating.</summary>
    public TestResourceWait? Status { get; }

    public IReadOnlyList<string> Warnings { get; }

    public TestProjectResources Resources => new(Needs, Status, Warnings);

    public static TestResourceLease Uncoordinated(TestResourceNeeds needs) => new(needs, null, [], null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _release?.Invoke();
        }
    }
}
