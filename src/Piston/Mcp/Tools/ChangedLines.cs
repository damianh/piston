// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Piston.Engine.Services;

namespace Piston.Mcp.Tools;

/// <summary>
/// Lines added or modified in the working tree relative to a git base, read with <c>git diff --unified=0</c>.
/// </summary>
internal static partial class ChangedLines
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Git revisions the tool accepts: names, hashes and suffixes such as <c>~1</c> or <c>@{u}</c>, never options.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9._/@{}~^][A-Za-z0-9._/@{}~^-]*$")]
    private static partial Regex RevisionPattern();

    [GeneratedRegex(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeader();

    public static bool IsValidRevision(string revision) => revision.Length <= 200 && RevisionPattern().IsMatch(revision);

    /// <summary>The git object name of the empty tree, used as the parent of a root commit.</summary>
    private const string EmptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

    /// <summary>Bases tried, in order, when <see cref="ChangeBase.Branch"/> has no ref and origin/HEAD is not set.</summary>
    private static readonly string[] DefaultBranchCandidates = ["origin/main", "origin/master", "main", "master"];

    /// <summary>Parses a base name; null or empty means <see cref="ChangeBase.Working"/>, or <see cref="ChangeBase.Branch"/> with a ref.</summary>
    public static bool TryParseBase(string? text, string? reference, out ChangeBase kind)
    {
        ChangeBase? parsed = text?.Trim().ToLowerInvariant() switch
        {
            null or "" => string.IsNullOrWhiteSpace(reference) ? ChangeBase.Working : ChangeBase.Branch,
            "working" => ChangeBase.Working,
            "branch" => ChangeBase.Branch,
            "range" => ChangeBase.Range,
            _ => null,
        };
        kind = parsed ?? ChangeBase.Working;
        return parsed is not null;
    }

    /// <summary>Validates a base and ref given by a client, then reads the changes they select.</summary>
    /// <exception cref="ArgumentException"><paramref name="base"/> or <paramref name="reference"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">Git failed, a ref is unknown or no base was found.</exception>
    public static Task<ChangedLinesResult> GetCheckedAsync(
        string directory, string? @base, string? reference, CancellationToken ct) =>
        GetCheckedAsync(directory, @base, reference, contextLines: 0, ct);

    /// <summary>
    /// Validates a base and ref given by a client, then reads the changes they select. With
    /// <paramref name="contextLines"/> above zero, the result also has each file's hunks with that much context.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="base"/> or <paramref name="reference"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">Git failed, a ref is unknown or no base was found.</exception>
    public static Task<ChangedLinesResult> GetCheckedAsync(
        string directory, string? @base, string? reference, int contextLines, CancellationToken ct)
    {
        if (!TryParseBase(@base, reference, out var kind))
        {
            throw new ArgumentException($"'{@base}' is not a base: use working, branch or range.", nameof(@base));
        }

        if (!string.IsNullOrWhiteSpace(reference) && !IsValidRevision(reference))
        {
            throw new ArgumentException($"'{reference}' is not a valid git revision.", nameof(reference));
        }

        return GetAsync(directory, kind, reference, contextLines, ct);
    }

    /// <summary>
    /// Returns each changed file's absolute path with its added or modified line numbers. Untracked files count
    /// as wholly changed. With a <paramref name="baseRevision"/>, changes are taken since its merge base with HEAD.
    /// </summary>
    public static Task<ChangedLinesResult> GetAsync(string directory, string? baseRevision, CancellationToken ct) =>
        GetAsync(directory, string.IsNullOrWhiteSpace(baseRevision) ? ChangeBase.Working : ChangeBase.Branch, baseRevision, ct);

    /// <summary>
    /// Returns each changed file's absolute path with its added or modified line numbers (new side).
    /// <list type="bullet">
    /// <item><see cref="ChangeBase.Working"/>: staged, unstaged and untracked changes against HEAD.</item>
    /// <item><see cref="ChangeBase.Branch"/>: the working tree, untracked files included, against the merge base of
    /// <paramref name="reference"/> (default: origin/HEAD, else origin/main, origin/master, main or master) and HEAD.
    /// Without a merge base, as in a shallow clone, it compares with the ref directly and adds a warning.</item>
    /// <item><see cref="ChangeBase.Range"/>: <c>A..B</c> (A against B), <c>A...B</c> (the merge base of A and B against
    /// B) or a single commit (its own changes). An empty side means HEAD.</item>
    /// </list>
    /// </summary>
    /// <exception cref="InvalidOperationException">Git failed, a ref is unknown or invalid, or no base was found.</exception>
    public static Task<ChangedLinesResult> GetAsync(
        string directory, ChangeBase kind, string? reference, CancellationToken ct) =>
        GetAsync(directory, kind, reference, contextLines: 0, ct);

    /// <summary>
    /// As <see cref="GetAsync(string, ChangeBase, string?, CancellationToken)"/>; with <paramref name="contextLines"/>
    /// above zero, also returns each changed file's hunks (<see cref="ChangedLinesResult.Diff"/>) with that many
    /// lines of context, read from the same diff as the changed lines.
    /// </summary>
    /// <exception cref="InvalidOperationException">Git failed, a ref is unknown or invalid, or no base was found.</exception>
    public static async Task<ChangedLinesResult> GetAsync(
        string directory, ChangeBase kind, string? reference, int contextLines, CancellationToken ct)
    {
        var top = (await RunGitAsync(directory, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim();
        var warnings = new List<string>();
        string basis;
        string[] revisions;
        var includeUntracked = true;
        string? newSide = null;
        switch (kind)
        {
            case ChangeBase.Branch:
                {
                    var target = string.IsNullOrWhiteSpace(reference)
                        ? await DefaultBranchAsync(top, ct).ConfigureAwait(false)
                            ?? throw new InvalidOperationException(
                                "No base branch found (origin/HEAD, origin/main, origin/master, main, master): pass baseRef.")
                        : RequireRevision(reference);
                    await RequireKnownAsync(top, target, ct).ConfigureAwait(false);
                    var mergeBase = await TryRunGitAsync(top, ["merge-base", target, "HEAD"], ct).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(mergeBase))
                    {
                        warnings.Add($"{target} and HEAD have no merge base (shallow clone?): compared with {target} directly.");
                        basis = target;
                    }
                    else
                    {
                        basis = mergeBase.Trim();
                    }

                    revisions = [basis];
                    break;
                }

            case ChangeBase.Range:
                {
                    if (string.IsNullOrWhiteSpace(reference))
                    {
                        throw new InvalidOperationException("A range needs baseRef, e.g. main..HEAD, main...HEAD or a commit.");
                    }

                    includeUntracked = false;
                    basis = reference;
                    var threeDot = reference.IndexOf("...", StringComparison.Ordinal);
                    var twoDot = reference.IndexOf("..", StringComparison.Ordinal);
                    if (threeDot >= 0 || twoDot >= 0)
                    {
                        var (left, right) = threeDot >= 0
                            ? (reference[..threeDot], reference[(threeDot + 3)..])
                            : (reference[..twoDot], reference[(twoDot + 2)..]);
                        var from = Side(left);
                        var to = Side(right);
                        await RequireKnownAsync(top, from, ct).ConfigureAwait(false);
                        await RequireKnownAsync(top, to, ct).ConfigureAwait(false);
                        if (threeDot >= 0)
                        {
                            from = (await TryRunGitAsync(top, ["merge-base", from, to], ct).ConfigureAwait(false))?.Trim() is { Length: > 0 } mb
                                ? mb
                                : throw new InvalidOperationException($"{from} and {to} have no merge base.");
                        }

                        revisions = [from, to];
                        newSide = to;
                    }
                    else
                    {
                        var commit = RequireRevision(reference);
                        await RequireKnownAsync(top, commit, ct).ConfigureAwait(false);
                        var parent = await TryRunGitAsync(top, ["rev-parse", "--verify", "--quiet", commit + "^"], ct)
                            .ConfigureAwait(false);
                        revisions = [string.IsNullOrWhiteSpace(parent) ? EmptyTree : parent.Trim(), commit];
                        newSide = commit;
                    }

                    break;
                }

            default:
                basis = "HEAD";
                revisions = ["HEAD"];
                break;
        }

        var withHunks = contextLines > 0;
        var diff = await RunGitAsync(
            top,
            ["-c", "core.quotepath=off", "diff", $"--unified={Math.Max(contextLines, 0)}", "--no-color", "--no-ext-diff",
                "--no-renames", .. revisions, "--"],
            ct).ConfigureAwait(false);
        var files = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        var hunks = new Dictionary<string, DiffFile>(StringComparer.Ordinal);
        if (withHunks)
        {
            foreach (var (path, file) in UnifiedDiff.Parse(diff))
            {
                var full = Path.GetFullPath(Path.Combine(top, path));
                hunks[full] = file;
                var added = new SortedSet<int>(file.AddedLines);
                if (added.Count > 0)
                {
                    files[full] = added;
                }
            }
        }
        else
        {
            foreach (var (path, lines) in ParseAddedLines(diff))
            {
                files[Path.GetFullPath(Path.Combine(top, path))] = lines;
            }
        }

        if (includeUntracked)
        {
            var untracked = await RunGitAsync(top, ["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
            foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.GetFullPath(Path.Combine(top, path));
                if (withHunks)
                {
                    var file = ReadUntracked(full);
                    hunks[full] = file;
                    files[full] = new SortedSet<int>(file.AddedLines);
                }
                else
                {
                    files[full] = new SortedSet<int>(Enumerable.Range(1, CountLines(full)));
                }
            }
        }

        var diverged = new HashSet<string>(StringComparer.Ordinal);
        if (newSide is not null && files.Count > 0)
        {
            // A range's line numbers are those of its last revision; files that differ in the working tree have
            // other content at those numbers.
            var differing = await RunGitAsync(
                top, ["-c", "core.quotepath=off", "diff", "--name-only", "-z", "--no-renames", newSide, "--"], ct).ConfigureAwait(false);
            foreach (var path in differing.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var full = Path.GetFullPath(Path.Combine(top, path));
                if (files.ContainsKey(full))
                {
                    _ = diverged.Add(full);
                }
            }

            if (diverged.Count > 0)
            {
                warnings.Add(
                    $"{diverged.Count} changed file(s) differ between {newSide} and the working tree: their line numbers " +
                    "do not match the working tree.");
            }
        }

        return new ChangedLinesResult(basis, files) { Warnings = warnings, DivergedFiles = diverged, Diff = hunks };

        static string Side(string side) => side.Length == 0 ? "HEAD" : RequireRevision(side);
    }

    private static string RequireRevision(string revision) =>
        IsValidRevision(revision) && !revision.Contains("..", StringComparison.Ordinal)
            ? revision
            : throw new InvalidOperationException($"'{revision}' is not a valid git revision.");

    private static async Task RequireKnownAsync(string top, string revision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(
            await TryRunGitAsync(top, ["rev-parse", "--verify", "--quiet", revision + "^{commit}"], ct).ConfigureAwait(false)))
        {
            throw new InvalidOperationException($"'{revision}' is not a known commit.");
        }
    }

    private static async Task<string?> DefaultBranchAsync(string top, CancellationToken ct)
    {
        var originHead = await TryRunGitAsync(top, ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(originHead))
        {
            return originHead.Trim();
        }

        foreach (var candidate in DefaultBranchCandidates)
        {
            if (!string.IsNullOrWhiteSpace(
                await TryRunGitAsync(top, ["rev-parse", "--verify", "--quiet", candidate + "^{commit}"], ct).ConfigureAwait(false)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static async Task<string?> TryRunGitAsync(string directory, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            return await RunGitAsync(directory, args, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Parses <c>git diff --unified=0</c> output into repository-relative paths and added line numbers.</summary>
    public static Dictionary<string, SortedSet<int>> ParseAddedLines(string diff)
    {
        var result = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        SortedSet<int>? current = null;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var target = line[4..].TrimEnd('\r');
                if (target == "/dev/null")
                {
                    current = null;
                    continue;
                }

                if (target.Length >= 2 && target[0] == '"' && target[^1] == '"')
                {
                    target = target[1..^1];
                }

                var path = target.StartsWith("b/", StringComparison.Ordinal) ? target[2..] : target;
                if (!result.TryGetValue(path, out current))
                {
                    current = [];
                    result[path] = current;
                }

                continue;
            }

            if (current is not null && HunkHeader().Match(line) is { Success: true } hunk)
            {
                var start = int.Parse(hunk.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                var count = hunk.Groups[2].Success
                    ? int.Parse(hunk.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : 1;
                for (var n = start; n < start + count; n++)
                {
                    _ = current.Add(n);
                }
            }
        }

        foreach (var empty in result.Where(kvp => kvp.Value.Count == 0).Select(kvp => kvp.Key).ToList())
        {
            _ = result.Remove(empty);
        }

        return result;
    }

    /// <summary>Formats line numbers as compact ranges, e.g. <c>3-5, 9</c>.</summary>
    public static string FormatRanges(IEnumerable<int> lines)
    {
        var sb = new StringBuilder();
        int? start = null, previous = null;
        foreach (var line in lines.Order())
        {
            if (previous is { } p && line == p + 1)
            {
                previous = line;
                continue;
            }

            Append();
            start = previous = line;
        }

        Append();
        return sb.ToString();

        void Append()
        {
            if (start is null)
            {
                return;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append(", ");
            }

            _ = sb.Append(start == previous ? $"{start}" : $"{start}-{previous}");
        }
    }

    /// <summary>An untracked file as wholly added; binary or unreadable files, and those over 4 MB, have no lines.</summary>
    private static DiffFile ReadUntracked(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 4 * 1024 * 1024)
            {
                return new DiffFile(DiffFileStatus.Added, []);
            }

            var bytes = File.ReadAllBytes(path);
            if (bytes.AsSpan(0, Math.Min(bytes.Length, 8000)).Contains((byte)0))
            {
                // Git's own test for binary content.
                return new DiffFile(DiffFileStatus.Binary, []);
            }

            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            return UnifiedDiff.Added(lines);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new DiffFile(DiffFileStatus.Added, []);
        }
    }

    private static int CountLines(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length <= 4 * 1024 * 1024 ? File.ReadLines(path).Count() : 0;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static async Task<string> RunGitAsync(string directory, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        _ = ChildProcessEnvironment.Prepare(psi);
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start git.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', args)} failed: {(await stderr.ConfigureAwait(false)).Trim()}");
            }

            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
    }
}

/// <summary>Changed lines per absolute file path, relative to <paramref name="Basis"/>.</summary>
internal sealed record ChangedLinesResult(string Basis, IReadOnlyDictionary<string, SortedSet<int>> Files)
{
    /// <summary>Caveats about the comparison, such as a missing merge base.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// For a range: changed files whose content at the range's last revision differs from the working tree, so their
    /// line numbers do not describe the files on disk.
    /// </summary>
    public IReadOnlySet<string> DivergedFiles { get; init; } = new HashSet<string>();

    /// <summary>
    /// Each changed file's hunks, by absolute path, when context lines were asked for: every file git lists, so
    /// including deleted, binary and non-source files, and those whose only changes are removals.
    /// </summary>
    public IReadOnlyDictionary<string, DiffFile> Diff { get; init; } = new Dictionary<string, DiffFile>();
}

/// <summary>What changes are compared against: see <see cref="ChangedLines.GetAsync(string, ChangeBase, string?, CancellationToken)"/>.</summary>
internal enum ChangeBase
{
    Working,
    Branch,
    Range,
}
