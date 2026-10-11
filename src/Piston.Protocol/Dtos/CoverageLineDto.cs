// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Coverage status for a single line of source code.</summary>
public sealed record CoverageLineDto(
    int LineNumber,
    int HitCount,
    string Status
);
