// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request the unified diff of the changes, with a verdict for each added line of the changed source files.</summary>
/// <param name="Base"><c>working</c>, <c>branch</c> or <c>range</c>; null means working, or branch when <paramref name="BaseRef"/> is set.</param>
/// <param name="BaseRef">For branch: the branch or commit to compare against. For range: the range or commit.</param>
/// <param name="Context">Unchanged lines shown around each change, from 1 to 20.</param>
public sealed record GetChangeDiffCommand(string? Base = null, string? BaseRef = null, int Context = 3);
