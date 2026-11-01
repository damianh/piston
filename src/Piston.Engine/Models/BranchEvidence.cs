// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Branch outcomes attributed to one measured unit on one source line.</summary>
public sealed record BranchEvidence(int Line, int Covered, int Total);
