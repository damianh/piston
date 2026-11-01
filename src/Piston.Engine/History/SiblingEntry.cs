// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.History;

/// <summary>A result recorded by another worktree of the same repository.</summary>
/// <param name="Worktree">The other worktree's folder name.</param>
/// <param name="Entry">The recorded result; it always has a source fingerprint.</param>
internal sealed record SiblingEntry(string Worktree, TestHistoryEntry Entry);
