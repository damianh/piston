// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>The outcome of running one test project.</summary>
public sealed record TestRunProjectOutcomeDto(
    string ProjectPath,
    string ProjectId,
    string Status,
    int TestCount,
    string? NoTestsReason,
    string? TimedOutTest,
    double? TestTimeoutMs);
