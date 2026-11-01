// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine;

/// <summary>
/// Controls which test execution backend is used.
/// </summary>
public enum TestExecutionMode
{
    /// <summary>
    /// Use MTP v2 in-process when detected; fall back to process-based execution.
    /// </summary>
    Auto,

    /// <summary>
    /// Always use <c>dotnet test</c> process-based execution. Safe universal fallback.
    /// </summary>
    Process,

    /// <summary>
    /// Always use MTP v2 in-process execution. Fails if the project is incompatible.
    /// </summary>
    InProcess,
}
