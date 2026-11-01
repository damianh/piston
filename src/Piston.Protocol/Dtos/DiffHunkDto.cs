// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A hunk of a unified diff, numbered as in <c>@@ -OldStart,OldCount +NewStart,NewCount @@</c>.</summary>
public sealed record DiffHunkDto(int OldStart, int OldCount, int NewStart, int NewCount, IReadOnlyList<DiffLineDto> Lines);
