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

    /// <summary>
    /// Returns each changed file's absolute path with its added or modified line numbers. Untracked files count
    /// as wholly changed. With a <paramref name="baseRevision"/>, changes are taken since its merge base with HEAD.
    /// </summary>
    public static async Task<ChangedLinesResult> GetAsync(string directory, string? baseRevision, CancellationToken ct)
    {
        var top = (await RunGitAsync(directory, ["rev-parse", "--show-toplevel"], ct).ConfigureAwait(false)).Trim();
        var basis = "HEAD";
        if (!string.IsNullOrWhiteSpace(baseRevision))
        {
            basis = (await RunGitAsync(directory, ["merge-base", baseRevision, "HEAD"], ct).ConfigureAwait(false)).Trim();
        }

        var diff = await RunGitAsync(
            top,
            ["-c", "core.quotepath=off", "diff", "--unified=0", "--no-color", "--no-ext-diff", "--no-renames", basis, "--"],
            ct).ConfigureAwait(false);
        var files = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var (path, lines) in ParseAddedLines(diff))
        {
            files[Path.GetFullPath(Path.Combine(top, path))] = lines;
        }

        var untracked = await RunGitAsync(top, ["ls-files", "--others", "--exclude-standard", "-z"], ct).ConfigureAwait(false);
        foreach (var path in untracked.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var full = Path.GetFullPath(Path.Combine(top, path));
            files[full] = new SortedSet<int>(Enumerable.Range(1, CountLines(full)));
        }

        return new ChangedLinesResult(basis, files);
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
internal sealed record ChangedLinesResult(string Basis, IReadOnlyDictionary<string, SortedSet<int>> Files);
