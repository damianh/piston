// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>An inclusive, 1-based range of source lines.</summary>
public sealed record CoveredLineRange(int Start, int End);
