// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>What caused a test run to be requested.</summary>
public enum TestRunTrigger
{
    /// <summary>The initial run issued when the engine starts.</summary>
    Startup,

    /// <summary>An explicit full run request (MCP <c>RunTests</c>, protocol force-run).</summary>
    Forced,

    /// <summary>A file-watcher change batch (normally a selective run).</summary>
    FileChange,

    /// <summary>A request to run selected tests, such as a node in the dashboard's test tree.</summary>
    Targeted,
}
