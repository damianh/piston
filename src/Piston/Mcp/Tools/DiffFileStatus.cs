// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>How a file differs from the base.</summary>
internal enum DiffFileStatus
{
    Modified,
    Added,
    Deleted,

    /// <summary>Git reports the file as binary, so it has no hunks.</summary>
    Binary,
}
