// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>Verdicts for every changed line of every changed source file.</summary>
internal sealed record ChangedLineCoverageResult(
    string Basis, IReadOnlyList<ChangedFileVerdicts> Files, IReadOnlyList<string> Warnings)
{
    public ChangedLineTotals Totals => ChangedLineTotals.Of(Files.SelectMany(f => f.Lines));

    /// <summary>Each changed file's hunks, by absolute path, when context lines were asked for; otherwise empty.</summary>
    public IReadOnlyDictionary<string, DiffFile> Diff { get; init; } = new Dictionary<string, DiffFile>();

    /// <summary>As <see cref="ChangedLinesResult.DivergedFiles"/>: files whose line numbers do not describe the files on disk.</summary>
    public IReadOnlySet<string> DivergedFiles { get; init; } = new HashSet<string>();

    /// <summary>Unknown lines per reason, every reason included.</summary>
    public IReadOnlyDictionary<UnknownReason, int> UnknownReasons
    {
        get
        {
            var counts = Enum.GetValues<UnknownReason>().Where(r => r != UnknownReason.None).ToDictionary(r => r, _ => 0);
            foreach (var line in Files.SelectMany(f => f.Lines).Where(l => l.Verdict == ChangedLineVerdict.Unknown))
            {
                counts[line.Reason]++;
            }

            return counts;
        }
    }
}
