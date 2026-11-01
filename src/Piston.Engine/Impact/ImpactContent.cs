// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace Piston.Engine.Impact;

internal static class ImpactContent
{
    /// <summary>Hex SHA-256 of the UTF-8 encoding of <paramref name="content"/>.</summary>
    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Reads a file for the impact map, or returns null when it cannot be read.</summary>
    public static string? TryRead(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath);
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
