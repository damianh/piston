// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record TestResult(
    string FullyQualifiedName,
    string DisplayName,
    TestStatus Status,
    TimeSpan Duration,
    string? Output,
    string? ErrorMessage,
    string? StackTrace,
    string? Source
);
