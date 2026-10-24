// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A source file's text, or why it can't be shown.</summary>
/// <param name="FilePath">The requested path, made absolute.</param>
/// <param name="Lines">The file's lines, or null when it can't be shown.</param>
/// <param name="Unavailable">Why <paramref name="Lines"/> is null, e.g. the file is outside the solution.</param>
public sealed record SourceFileDto(string FilePath, IReadOnlyList<string>? Lines, string? Unavailable = null);
