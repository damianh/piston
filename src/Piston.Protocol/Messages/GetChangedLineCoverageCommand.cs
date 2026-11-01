// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Request a verdict for each changed line of the changed source files.</summary>
/// <param name="Base"><c>working</c>, <c>branch</c> or <c>range</c>; null means working, or branch when <paramref name="BaseRef"/> is set.</param>
/// <param name="BaseRef">For branch: the branch or commit to compare against. For range: the range or commit.</param>
public sealed record GetChangedLineCoverageCommand(string? Base = null, string? BaseRef = null);
