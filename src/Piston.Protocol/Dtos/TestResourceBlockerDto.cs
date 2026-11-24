// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// Another test project run, possibly in another worktree, that holds (<c>Held</c>) or is queued ahead for
/// (<c>Queued</c>) a resource. <see cref="Resource"/> is a resource key.
/// </summary>
public sealed record TestResourceBlockerDto(
    string Resource,
    string Kind,
    int ProcessId,
    string? Worktree,
    string? Project,
    DateTimeOffset Since
);
