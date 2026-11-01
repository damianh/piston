// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>One line of a hunk.</summary>
/// <param name="OldLine">The line's number in the base, for context and removed lines.</param>
/// <param name="NewLine">The line's number in the changed file, for context and added lines.</param>
/// <param name="Text">The line's text, without its line ending.</param>
internal sealed record DiffLine(DiffLineKind Kind, int? OldLine, int? NewLine, string Text);
