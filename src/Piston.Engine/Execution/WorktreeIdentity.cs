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

    /// <summary>
    /// Resolves links and canonicalizes existing path spelling, including on case-sensitive macOS
    /// volumes. Missing suffixes retain their spelling relative to the nearest existing ancestor.
    /// </summary>
    public static string CanonicalizePath(string path)
    {
        var resolved = Path.TrimEndingDirectorySeparator(ResolveLinks(Path.GetFullPath(path)));
        if (!OperatingSystem.IsMacOS())
        {
            return OperatingSystem.IsWindows() ? resolved.ToLowerInvariant() : resolved;
        }

        var suffix = new Stack<string>();
        var ancestor = resolved;
        while (!File.Exists(ancestor) && !Directory.Exists(ancestor))
        {
            suffix.Push(Path.GetFileName(ancestor));
            ancestor = Path.GetDirectoryName(ancestor)
                ?? throw new ExecutionCoordinationException($"Cannot canonicalize path '{path}'.");
        }

        var canonical = GetExistingPathSpelling(ancestor);
        foreach (var segment in suffix)
        {
            canonical = Path.Combine(canonical, segment);
        }
        return Path.TrimEndingDirectorySeparator(canonical);
    }

    public static WorktreeIdentity FromSolutionPath(string solutionPath)
    {
        var full = Path.GetFullPath(solutionPath);
        var resolvedPath = ResolveLinks(full);
        var resolved = Directory.Exists(resolvedPath) ? resolvedPath : Path.GetDirectoryName(resolvedPath) ?? resolvedPath;

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
        if (OperatingSystem.IsMacOS())
        {
            return GetExistingPathSpelling(trimmed);
        }

        return OperatingSystem.IsWindows() ? trimmed.ToLowerInvariant() : trimmed;
    }

    internal static string GetExistingPathSpelling(string path)
    {
        try
        {
            var current = Path.GetPathRoot(path)!;
            foreach (var segment in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                // macOS treats canonical Unicode spellings as equivalent, including on case-sensitive volumes.
                var comparableSegment = ComparableSpelling(segment);
                var matches = Directory.EnumerateFileSystemEntries(current)
                    .Where(entry => string.Equals(ComparableSpelling(Path.GetFileName(entry)), comparableSegment, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                current = matches.FirstOrDefault(entry => string.Equals(Path.GetFileName(entry), segment, StringComparison.Ordinal))
                    ?? matches.FirstOrDefault(entry => string.Equals(ComparableSpelling(Path.GetFileName(entry)), comparableSegment, StringComparison.Ordinal))
                    ?? (matches.Count == 1 ? matches[0] : throw new IOException($"Cannot resolve the spelling of '{segment}'."));
            }

            return Path.TrimEndingDirectorySeparator(current);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ExecutionCoordinationException(
                $"Cannot canonicalize worktree '{path}': {ex.Message}", ex);
        }
    }

    private static string ComparableSpelling(string name) =>
        OperatingSystem.IsMacOS() ? name.Normalize(NormalizationForm.FormC) : name;

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
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            var candidate = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            if (info.LinkTarget is { } target)
            {
                if (++depth > MaxLinkDepth)
                {
                    throw new ExecutionCoordinationException($"Too many symbolic links resolving '{path}'.");
                }

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

/// <summary>Details recorded by a worktree owner for diagnostics when another daemon is refused.</summary>
/// <param name="Host">How the owner was started (<c>daemon</c>, <c>mcp</c>, ...); null when not recorded.</param>
public sealed record ExecutionOwnerInfo(
    int ProcessId, string SolutionPath, string WorktreePath, DateTimeOffset AcquiredAt, string? Host = null);
