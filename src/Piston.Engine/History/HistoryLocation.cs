// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using Piston.Engine.Execution;

namespace Piston.Engine.History;

/// <summary>
/// Where a solution's run history lives: outside the checkout, under a per-user directory, grouped by
/// repository identity (the git common directory, so every worktree of a repository shares one folder)
/// with one subfolder per worktree. Deleting a worktree does not delete its history.
/// </summary>
/// <remarks>
/// <c>&lt;base&gt;/&lt;repository&gt;-&lt;hash&gt;/&lt;worktree&gt;-&lt;hash&gt;/&lt;solution&gt;.history.json</c>.
/// Outside git the solution directory stands in for both the repository and the worktree.
/// </remarks>
public sealed record HistoryLocation(string RepositoryPath, string WorktreePath, string FilePath)
{
    /// <summary>Overrides the default base directory; must be an absolute path.</summary>
    public const string EnvironmentVariable = "PISTON_HISTORY_DIRECTORY";

    public string Directory => Path.GetDirectoryName(FilePath)!;

    public string LockPath => FilePath + ".lock";

    /// <summary>
    /// <see cref="EnvironmentVariable"/> when set, otherwise <c>Piston/history</c> under the per-user local
    /// application data folder.
    /// </summary>
    public static string DefaultBaseDirectory() => DefaultBaseDirectory(
        Environment.GetEnvironmentVariable,
        () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify));

    internal static string DefaultBaseDirectory(Func<string, string?> getEnvironmentVariable, Func<string> getLocalApplicationData)
    {
        if (getEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured)
        {
            return Path.IsPathFullyQualified(configured)
                ? configured
                : throw new InvalidOperationException($"{EnvironmentVariable} must be an absolute path; got '{configured}'.");
        }

        var local = getLocalApplicationData();
        return string.IsNullOrEmpty(local)
            ? throw new InvalidOperationException(
                $"Cannot determine the per-user local application data folder for run history. Set {EnvironmentVariable} to an absolute path.")
            : Path.Combine(local, "Piston", "history");
    }

    public static HistoryLocation Resolve(string baseDirectory, string solutionPath)
    {
        var worktree = WorktreeIdentity.FromSolutionPath(solutionPath);
        var repository = FindRepository(worktree.RootPath) ?? worktree.RootPath;
        var solution = Path.GetFileNameWithoutExtension(solutionPath);
        var file = Path.Combine(
            baseDirectory,
            Folder(RepositoryLabel(repository), repository),
            Folder(Path.GetFileName(worktree.RootPath), worktree.RootPath),
            Sanitize(solution) + ".history.json");
        return new HistoryLocation(repository, worktree.RootPath, file);
    }

    /// <summary>
    /// The git common directory of a worktree root: <c>.git</c> itself, or for a linked worktree (whose
    /// <c>.git</c> is a <c>gitdir:</c> file) the directory its <c>commondir</c> names. Null outside git.
    /// </summary>
    internal static string? FindRepository(string worktreeRoot)
    {
        var git = Path.Combine(worktreeRoot, ".git");
        if (System.IO.Directory.Exists(git))
        {
            return Canonical(git);
        }

        if (!File.Exists(git))
        {
            return null;
        }

        try
        {
            var line = File.ReadLines(git).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line is null)
            {
                return null;
            }

            var gitDir = Path.GetFullPath(line["gitdir:".Length..].Trim(), worktreeRoot);
            var commonDirFile = Path.Combine(gitDir, "commondir");
            if (!File.Exists(commonDirFile))
            {
                return Canonical(gitDir);
            }

            var common = File.ReadAllText(commonDirFile).Trim();
            return Canonical(Path.GetFullPath(common, gitDir));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Canonical(string path) => WorktreeIdentity.CanonicalizePath(path);

    // "/src/app/.git" -> "app"; a bare "/src/app.git" -> "app".
    private static string RepositoryLabel(string repository)
    {
        var name = Path.GetFileName(repository);
        if (name == ".git")
        {
            return Path.GetFileName(Path.GetDirectoryName(repository)) ?? "repository";
        }

        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static string Folder(string? label, string identity)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];
        return $"{Sanitize(string.IsNullOrEmpty(label) ? "root" : label)}-{hash}";
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var text = new string([.. name.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c)]);
        return text.Length > 40 ? text[..40] : text;
    }
}
