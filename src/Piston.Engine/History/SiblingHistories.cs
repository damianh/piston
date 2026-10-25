// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.History;

/// <summary>A result recorded by another worktree of the same repository.</summary>
/// <param name="Worktree">The other worktree's folder name.</param>
/// <param name="Entry">The recorded result; it always has a source fingerprint.</param>
internal sealed record SiblingEntry(string Worktree, TestHistoryEntry Entry);

/// <summary>
/// Reads, without locking, the run history files that other worktrees of the same repository keep for a solution
/// with the same file name, so flaky detection can match outcomes on one source fingerprint across worktrees.
/// Each file is parsed again only when its write time or length changes; unreadable files are skipped.
/// Only results with a source fingerprint are kept, because only those can be matched.
/// </summary>
internal sealed class SiblingHistories
{
    private readonly object _gate = new();
    private readonly string _repositoryDirectory;
    private readonly string _ownDirectory;
    private readonly string _fileName;
    private readonly Action<string>? _log;
    private readonly Dictionary<string, CachedFile> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedFailures = new(StringComparer.Ordinal);
    private IReadOnlyDictionary<(string Project, string Test), IReadOnlyList<SiblingEntry>> _merged =
        new Dictionary<(string Project, string Test), IReadOnlyList<SiblingEntry>>();

    public SiblingHistories(HistoryLocation location, Action<string>? log = null)
    {
        _ownDirectory = Path.TrimEndingDirectorySeparator(location.Directory);
        _repositoryDirectory = Path.GetDirectoryName(_ownDirectory) ?? _ownDirectory;
        _fileName = Path.GetFileName(location.FilePath);
        _log = log;
    }

    /// <summary>Results recorded by the other worktrees, keyed by project and test, refreshed from disk as needed.</summary>
    public IReadOnlyDictionary<(string Project, string Test), IReadOnlyList<SiblingEntry>> Snapshot()
    {
        lock (_gate)
        {
            var changed = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in Candidates())
            {
                _ = seen.Add(path);
                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists)
                    {
                        continue;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (_files.TryGetValue(path, out var cached) && cached.WriteTime == info.LastWriteTimeUtc && cached.Length == info.Length)
                {
                    continue;
                }

                _files[path] = Read(path, info);
                changed = true;
            }

            foreach (var gone in _files.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                _ = _files.Remove(gone);
                changed = true;
            }

            if (changed)
            {
                _merged = _files.Values
                    .SelectMany(f => f.Tests.SelectMany(pair => pair.Value.Select(e => (pair.Key, Entry: new SiblingEntry(f.Worktree, e)))))
                    .GroupBy(x => x.Key)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<SiblingEntry>)[.. g.Select(x => x.Entry)]);
            }

            return _merged;
        }
    }

    private IEnumerable<string> Candidates()
    {
        string[] directories;
        try
        {
            directories = Directory.GetDirectories(_repositoryDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return directories
            .Where(d => !string.Equals(Path.TrimEndingDirectorySeparator(d), _ownDirectory, StringComparison.Ordinal))
            .Select(d => Path.Combine(d, _fileName));
    }

    private CachedFile Read(string path, FileInfo info)
    {
        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        try
        {
            var (worktreePath, tests) = RunHistoryStore.ReadSourcedEntries(path);
            _ = _reportedFailures.Remove(path);
            var label = worktreePath is { Length: > 0 } ? Path.GetFileName(Path.TrimEndingDirectorySeparator(worktreePath)) : StripHash(folder);
            return new CachedFile(info.LastWriteTimeUtc, info.Length, string.IsNullOrEmpty(label) ? StripHash(folder) : label, tests);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException
            or IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            if (_reportedFailures.Add(path))
            {
                _log?.Invoke($"Cannot read run history of another worktree {path}: {ex.Message}; it is ignored.");
            }

            return new CachedFile(info.LastWriteTimeUtc, info.Length, StripHash(folder), new Dictionary<(string, string), IReadOnlyList<TestHistoryEntry>>());
        }
    }

    // "feature-0123456789abcdef" -> "feature"
    private static string StripHash(string folder) =>
        folder.LastIndexOf('-') is var dash and > 0 && folder.Length - dash - 1 == 16 ? folder[..dash] : folder;

    private sealed record CachedFile(
        DateTime WriteTime,
        long Length,
        string Worktree,
        IReadOnlyDictionary<(string Project, string Test), IReadOnlyList<TestHistoryEntry>> Tests);
}
