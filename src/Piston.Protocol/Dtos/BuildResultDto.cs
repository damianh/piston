// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record BuildResultDto(
    BuildStatusDto Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    double DurationMs,
    int OmittedErrors = 0,
    int OmittedWarnings = 0
);
