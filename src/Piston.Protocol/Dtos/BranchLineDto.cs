// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Branch outcomes attributed to a measured test on one source line.</summary>
public sealed record BranchLineDto(int Line, int Covered, int Total);
