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
