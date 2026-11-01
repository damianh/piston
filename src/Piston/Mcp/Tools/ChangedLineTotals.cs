// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Mcp.Tools;

/// <summary>Counts of changed lines per verdict.</summary>
internal sealed record ChangedLineTotals(
    int ChangedLines, int CoveredByPassing, int CoveredOnlyByFailing, int Uncovered, int NonExecutable, int Unknown)
{
    public static ChangedLineTotals Of(IEnumerable<ChangedLineResult> lines)
    {
        int total = 0, passing = 0, failing = 0, uncovered = 0, nonExecutable = 0, unknown = 0;
        foreach (var line in lines)
        {
            total++;
            switch (line.Verdict)
            {
                case ChangedLineVerdict.CoveredByPassing: passing++; break;
                case ChangedLineVerdict.CoveredOnlyByFailing: failing++; break;
                case ChangedLineVerdict.Uncovered: uncovered++; break;
                case ChangedLineVerdict.NonExecutable: nonExecutable++; break;
                default: unknown++; break;
            }
        }

        return new(total, passing, failing, uncovered, nonExecutable, unknown);
    }
}
