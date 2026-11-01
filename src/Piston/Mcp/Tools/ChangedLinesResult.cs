// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>Changed lines per absolute file path, relative to <paramref name="Basis"/>.</summary>
internal sealed record ChangedLinesResult(string Basis, IReadOnlyDictionary<string, SortedSet<int>> Files)
{
    /// <summary>Caveats about the comparison, such as a missing merge base.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// For a range: changed files (in <see cref="Files"/> or <see cref="Diff"/>) whose content at the range's last
    /// revision differs from the working tree, so their line numbers do not describe the files on disk.
    /// </summary>
    public IReadOnlySet<string> DivergedFiles { get; init; } = new HashSet<string>();

    /// <summary>
    /// Each changed file's hunks, by absolute path, when context lines were asked for: every file git lists, so
    /// including deleted, binary and non-source files, and those whose only changes are removals.
    /// </summary>
    public IReadOnlyDictionary<string, DiffFile> Diff { get; init; } = new Dictionary<string, DiffFile>();
}
