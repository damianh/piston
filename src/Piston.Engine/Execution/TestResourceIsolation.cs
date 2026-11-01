// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>How a test project's runs are isolated from runs of the same project in other worktrees.</summary>
public enum TestResourceIsolation
{
    /// <summary>
    /// One run of the project at a time per repository, across all its worktrees on the machine. The
    /// default for projects that use containers, which commonly reuse fixed container names and ports.
    /// </summary>
    Repository,

    /// <summary>No project-level isolation: only the declared exclusive resources are coordinated.</summary>
    None,
}
