// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;

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
