// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace Piston.Engine.Execution;

/// <summary>Stable identity of a clone, shared by its linked worktrees.</summary>
public sealed record CloneIdentity(string RootPath, string Key)
{
    private const int MaxMetadataLength = 4096;

    /// <summary>
    /// Resolves the clone's common Git directory without invoking Git. Invalid or unverified Git metadata
    /// is scoped to the worktree root rather than being allowed to claim another clone's trust.
    /// </summary>
    public static CloneIdentity FromWorktreePath(string path)
    {
        var worktree = WorktreeIdentity.FromSolutionPath(path);
        var gitPath = Path.Combine(worktree.RootPath, ".git");
        var commonDirectory = Directory.Exists(gitPath)
            ? gitPath
            : File.Exists(gitPath) ? ResolveCommonDirectory(worktree.RootPath, gitPath) : null;
        var root = WorktreeIdentity.CanonicalizePath(commonDirectory ?? worktree.RootPath);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..32];
        return new CloneIdentity(root, key);
    }

    private static string? ResolveCommonDirectory(string worktreeRoot, string gitFile)
    {
        try
        {
            var adminDirectory = ResolvePath(worktreeRoot, ReadPathFile(gitFile, "gitdir: "));
            var backlinkFile = Path.Combine(adminDirectory, "gitdir");
            if (!File.Exists(backlinkFile)
                || !PathsEqual(ResolvePath(adminDirectory, ReadPathFile(backlinkFile)), gitFile))
            {
                return null;
            }

            var commonFile = Path.Combine(adminDirectory, "commondir");
            if (!File.Exists(commonFile))
            {
                return null;
            }

            var commonDirectory = ResolvePath(adminDirectory, ReadPathFile(commonFile));
            var worktreesDirectory = ResolvePath(commonDirectory, "worktrees");
            return Directory.Exists(commonDirectory)
                && PathsEqual(Path.GetDirectoryName(adminDirectory)!, worktreesDirectory)
                ? commonDirectory
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string ReadPathFile(string path, string? prefix = null)
    {
        if (new FileInfo(path).Length > MaxMetadataLength)
        {
            throw new IOException($"Git metadata file '{path}' is too large.");
        }

        var value = File.ReadAllText(path).Trim();
        if (value.Length == 0 || value.Contains('\n') || value.Contains('\r')
            || (prefix is not null && !value.StartsWith(prefix, StringComparison.Ordinal)))
        {
            throw new IOException($"Git metadata file '{path}' is invalid.");
        }

        return prefix is null ? value : value[prefix.Length..];
    }

    private static string ResolvePath(string baseDirectory, string path) =>
        WorktreeIdentity.CanonicalizePath(Path.IsPathFullyQualified(path) ? path : Path.Combine(baseDirectory, path));

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, WorktreeIdentity.CanonicalizePath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
