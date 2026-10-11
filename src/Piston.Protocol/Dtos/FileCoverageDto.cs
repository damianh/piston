// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Per-file coverage data containing line-level hit counts.</summary>
public sealed record FileCoverageDto(
    string FilePath,
    IReadOnlyList<CoverageLineDto> Lines
);
