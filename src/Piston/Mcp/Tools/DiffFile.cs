// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>One changed file of a unified diff.</summary>
internal sealed record DiffFile(DiffFileStatus Status, IReadOnlyList<DiffHunk> Hunks)
{
    /// <summary>The added lines' numbers in the changed file.</summary>
    public IEnumerable<int> AddedLines =>
        Hunks.SelectMany(h => h.Lines).Where(l => l.Kind == DiffLineKind.Added).Select(l => l.NewLine!.Value);
}
