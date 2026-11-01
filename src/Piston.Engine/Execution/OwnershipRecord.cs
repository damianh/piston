// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Execution;

/// <summary>An ownership record found by <see cref="WorktreeOwnership.ListOwners"/>.</summary>
/// <param name="Owner">Recorded details; null when a live owner's details could not be read.</param>
public sealed record OwnershipRecord(string Key, OwnershipLiveness Liveness, ExecutionOwnerInfo? Owner);
