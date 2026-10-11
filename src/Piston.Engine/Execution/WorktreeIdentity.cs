// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace Piston.Engine.Execution;

/// <summary>
/// Canonical identity of the worktree containing a solution: symlinks resolved, then the nearest
/// ancestor holding a <c>.git</c> directory or file (git worktrees use a file). Solutions outside git
/// are identified by their own directory. Two daemons for different solutions in the same worktree
/// share one identity, because their builds and tests share the worktree's files.
/// </summary>
public sealed record WorktreeIdentity(string RootPath, string Key)
{
    private const int MaxLinkDepth = 40;

    public static WorktreeIdentity FromSolutionPath(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var start = Directory.Exists(full) ? full : Path.GetDirectoryName(full) ?? full;
        var resolved = ResolveLinks(start);

        var root = resolved;
        for (var dir = new DirectoryInfo(resolved); dir is not null; dir = dir.Parent)
        {
            var git = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                root = dir.FullName;
                break;
            }
        }

        root = Normalize(root);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(root));
        return new WorktreeIdentity(root, Convert.ToHexStringLower(hash)[..32]);
    }

    private static string Normalize(string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(path);
        // Default Windows and macOS filesystems are case-insensitive.
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? trimmed.ToLowerInvariant() : trimmed;
    }

    /// <summary>Resolves symbolic links in every segment of an absolute path.</summary>
    private static string ResolveLinks(string path)
    {
        var depth = 0;
        var root = Path.GetPathRoot(path)!;
        var current = root;
        var remaining = new Queue<string>(path[root.Length..]
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));

        while (remaining.Count > 0)
        {
            var segment = remaining.Dequeue();
            if (segment == ".") continue;
            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var candidate = Path.Combine(current, segment);
            var info = new DirectoryInfo(candidate);
            if (info.Exists && info.LinkTarget is { } target)
            {
                if (++depth > MaxLinkDepth)
                    throw new ExecutionCoordinationException($"Too many symbolic links resolving '{path}'.");
                var absolute = Path.IsPathFullyQualified(target) ? target : Path.Combine(current, target);
                var targetRoot = Path.GetPathRoot(absolute)!;
                var rest = remaining.ToArray();
                remaining = new Queue<string>(absolute[targetRoot.Length..]
                    .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                    .Concat(rest));
                current = targetRoot;
                continue;
            }

            current = candidate;
        }

        return current;
    }
}

/// <summary>Details a worktree owner records for diagnostics when another daemon is refused.</summary>
public sealed record ExecutionOwnerInfo(int ProcessId, string SolutionPath, string WorktreePath, DateTimeOffset AcquiredAt);
