// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Hosting.Configuration;

/// <summary>The <c>execution</c> section of <c>.piston.json</c>.</summary>
public sealed class PistonExecutionConfig
{
    /// <summary>
    /// Memory in MiB each run reserves from the machine execution budget. Must be positive.
    /// Required when the machine budget limits memory. It is an admission reservation, not a cap.
    /// </summary>
    public int? MemoryMb { get; set; }
}
