// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

public sealed record TestSuiteDto(
    string Name,
    IReadOnlyList<TestResultDto> Tests,
    DateTimeOffset Timestamp,
    double TotalDurationMs,
    string? RunId = null,
    bool Restored = false,
    string? ProjectId = null
);
