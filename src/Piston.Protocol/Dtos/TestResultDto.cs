// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record TestResultDto(
    string FullyQualifiedName,
    string DisplayName,
    TestStatusDto Status,
    double DurationMs,
    string? Output,
    string? ErrorMessage,
    string? StackTrace,
    string? Source
);
