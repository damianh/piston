// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using Piston.Engine.Impact;

namespace Piston.Engine.Coverage;

/// <summary>
/// A file's line coverage from one run, stamped with the hash of the source it was measured against.
/// </summary>
/// <param name="Coverage">The merged coverage for the file across every report in the run.</param>
/// <param name="ContentHash">SHA-256 of the file's content when the run finished, or null when unreadable.</param>
internal sealed record CoverageReportFile(FileCoverage Coverage, string? ContentHash, string? Content = null)
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
public sealed record CoverageFileSummary(
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
    DateTimeOffset UpdatedAt,
    string? SnapshotHash = null);

/// <summary>
/// The solution-wide coverage report: every measured file's totals.
/// </summary>
public sealed record CoverageReportSnapshot(IReadOnlyList<CoverageFileSummary> Files)
{
    public int LinesTotal => Files.Sum(f => f.LinesTotal);

    public int LinesCovered => Files.Sum(f => f.LinesCovered);

    public int BranchesTotal => Files.Sum(f => f.BranchesTotal);

    public int BranchesCovered => Files.Sum(f => f.BranchesCovered);

    /// <summary>When the report was last written, or null when it is empty.</summary>
    public DateTimeOffset? GeneratedAt => Files.Count == 0 ? null : Files.Max(f => f.UpdatedAt);
}

/// <summary>
/// One file's coverage totals and per-line coverage.
/// </summary>
public sealed record FileCoverageSnapshot(CoverageFileSummary File, IReadOnlyList<LineCoverage> Lines)
{
    /// <summary>Current source line ranges for which stored coverage could not be validated.</summary>
    public IReadOnlyList<CoveredLineRange> StaleLines { get; init; } = [];
}

/// <summary>An inclusive, 1-based range of source lines.</summary>
public sealed record CoveredLineRange(int Start, int End);

/// <summary>A test the impact map credits with executing a source file.</summary>
/// <param name="TestFqn">Fully-qualified test name.</param>
/// <param name="ProjectPath">The test project.</param>
/// <param name="Granularity">
/// How the evidence was measured: <c>run</c> (every test in the project's run together), <c>class</c> (the test's
/// class run alone) or <c>test</c> (the test alone). Run- and class-level evidence over-attributes: it does not mean
/// this test executed the file.
/// </param>
/// <param name="ExecutedLines">The executed method ranges, numbered as in the content that was measured.</param>
/// <param name="IsCurrent">
/// The file is unchanged since it was measured and the measured code has not changed since, so the ranges line up
/// with the file as it is now.
/// </param>
public sealed record CoveringTest(
    string TestFqn,
    string ProjectPath,
    string Granularity,
    IReadOnlyList<CoveredLineRange> ExecutedLines,
    bool IsCurrent);
