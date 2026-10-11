// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record BuildResult(
    BuildStatus Status,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    TimeSpan Duration
);
