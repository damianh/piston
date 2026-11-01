// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>A line of a stack trace, with the source location it names, if any.</summary>
/// <param name="Text">The line as written.</param>
/// <param name="FilePath">The source file, when the frame has one.</param>
/// <param name="Line">The 1-based line number in <paramref name="FilePath"/>.</param>
public sealed record StackFrameLine(string Text, string? FilePath, int? Line)
{
    public bool HasSource => FilePath is not null && Line is not null;
}
