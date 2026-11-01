// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>What changes are compared against: see <see cref="ChangedLines.GetAsync(string, ChangeBase, string?, CancellationToken)"/>.</summary>
internal enum ChangeBase
{
    Working,
    Branch,
    Range,
}
