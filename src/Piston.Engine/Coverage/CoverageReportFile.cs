// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;

namespace Piston.Engine.Coverage;

/// <summary>
/// A file's line coverage from one run, stamped with the hash of the source it was measured against.
/// </summary>
/// <param name="Coverage">The merged coverage for the file across every report in the run.</param>
/// <param name="ContentHash">SHA-256 of the file's content when the run finished, or null when unreadable.</param>
internal sealed record CoverageReportFile(FileCoverage Coverage, string? ContentHash)
{
    /// <summary>Returns the hex SHA-256 of <paramref name="filePath"/>, or null when it cannot be read.</summary>
    public static string? ComputeContentHash(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Stored coverage totals for one file in the solution-wide coverage report.
/// </summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="Module">Assembly the file was compiled into, when known.</param>
/// <param name="LinesTotal">Coverable lines.</param>
/// <param name="LinesCovered">Coverable lines executed at least once.</param>
/// <param name="BranchesTotal">Branch outcomes.</param>
/// <param name="BranchesCovered">Branch outcomes taken.</param>
/// <param name="IsStale">The file changed after it was measured and no run has re-measured it.</param>
/// <param name="IsPartial">The data comes from a selective run rather than a full run of every test.</param>
/// <param name="ContentHash">SHA-256 of the content the data was measured against.</param>
/// <param name="RunId">Coverage run that last wrote the file.</param>
/// <param name="UpdatedAt">When the file was last written.</param>
internal sealed record CoverageFileSummary(
    string FilePath,
    string? Module,
    int LinesTotal,
    int LinesCovered,
    int BranchesTotal,
    int BranchesCovered,
    bool IsStale,
    bool IsPartial,
    string? ContentHash,
    long RunId,
    DateTimeOffset UpdatedAt);
