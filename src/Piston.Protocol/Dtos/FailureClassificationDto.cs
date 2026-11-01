// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>How one failed test relates to the worktree's run history.</summary>
/// <param name="Kind">New, PreExisting, Flaky or Unknown.</param>
/// <param name="Evidence">The earlier runs the kind rests on.</param>
public sealed record FailureClassificationDto(string Project, string Test, string Kind, string Evidence, string? ProjectId = null);
