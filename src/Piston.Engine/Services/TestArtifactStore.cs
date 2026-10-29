// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Security;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

internal sealed class TestArtifactStore
{
    public const long MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxAttachmentsPerTest = 32;
    public const long MaxRunBytes = 256 * 1024 * 1024;

    private const int CopyBufferSize = 64 * 1024;
    private readonly string _solutionDirectory;
    private readonly string _rootDirectory;
    private readonly Action<string>? _log;
    private readonly ConcurrentDictionary<string, int> _nextIndex = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _runBytes = new(StringComparer.Ordinal);

    public TestArtifactStore(string solutionDirectory, Action<string>? log = null)
    {
        _solutionDirectory = Path.GetFullPath(solutionDirectory);
        _rootDirectory = Path.Combine(_solutionDirectory, ".piston", "artifacts");
        _log = log;
    }

    public IReadOnlyList<TestSuite> Retain(string runId, IReadOnlyList<TestSuite> suites)
    {
        var totalBytes = _runBytes.GetOrAdd(runId, 0);
        var retainedSuites = new List<TestSuite>(suites.Count);
        foreach (var suite in suites)
        {
            var retainedTests = new List<TestResult>(suite.Tests.Count);
            foreach (var test in suite.Tests)
            {
                var testAttachmentCount = 0;
                var attachments = new List<TestAttachment>(test.Attachments.Count);
                foreach (var attachment in test.Attachments)
                {
                    var index = _nextIndex.AddOrUpdate(runId, 1, (_, current) => current + 1) - 1;
                    var originalPath = attachment.Path;
                    var retained = RetainOne(runId, index, attachment, originalPath, testAttachmentCount, totalBytes);
                    if (retained.Retained)
                    {
                        testAttachmentCount++;
                        totalBytes += retained.SizeBytes ?? 0;
                    }

                    attachments.Add(retained);
                }

                retainedTests.Add(test with { Attachments = attachments });
            }

            retainedSuites.Add(suite with { Tests = retainedTests });
        }

        _runBytes[runId] = totalBytes;
        return retainedSuites;
    }

    public string? Resolve(TestAttachment attachment)
    {
        if (!attachment.Retained
            || string.IsNullOrWhiteSpace(attachment.Path)
            || string.IsNullOrWhiteSpace(attachment.RunId))
        {
            return null;
        }

        string path;
        string runDirectory;
        try
        {
            path = Path.GetFullPath(attachment.Path, _solutionDirectory);
            runDirectory = RunDirectory(attachment.RunId);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!IsWithin(_rootDirectory, path)
            || !string.Equals(Path.GetDirectoryName(path), runDirectory, PathComparison)
            || !Path.GetFileName(path).StartsWith($"{attachment.Index:D4}-", StringComparison.Ordinal)
            || !File.Exists(path)
            || HasReparsePointInPath(path))
        {
            return null;
        }

        return path;
    }

    public void Prune(IReadOnlyCollection<TestRunRecord> runs, IReadOnlyCollection<TestSuite> aggregateSuites)
    {
        if (!Directory.Exists(_rootDirectory) || HasReparsePointInPath(_rootDirectory))
        {
            return;
        }

        var attachments = runs.SelectMany(run => run.Tests)
            .Concat(aggregateSuites.SelectMany(suite => suite.Tests))
            .SelectMany(test => test.Attachments)
            .Where(attachment => attachment.Retained && IsValidRunId(attachment.RunId))
            .ToList();
        var kept = attachments.Select(attachment => attachment.RunId!).ToHashSet(StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(_rootDirectory))
        {
            if (!kept.Contains(Path.GetFileName(directory)))
            {
                TryDeleteDirectory(directory);
            }
        }

        foreach (var runAttachments in attachments.GroupBy(attachment => attachment.RunId!, StringComparer.Ordinal))
        {
            var runId = runAttachments.Key;
            var path = RunDirectory(runId);
            var referenced = runAttachments
                .Select(attachment => attachment.Index)
                .ToHashSet();
            if (!Directory.Exists(path) || HasReparsePointInPath(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path))
            {
                var name = Path.GetFileName(file);
                var separator = name.IndexOf('-', StringComparison.Ordinal);
                if (separator > 0 && int.TryParse(name[..separator], out var index) && !referenced.Contains(index))
                {
                    TryDeleteFile(file);
                }
            }
        }
    }

    public void Clear()
    {
        if (Directory.Exists(_rootDirectory))
        {
            TryDeleteDirectory(_rootDirectory);
        }

        _nextIndex.Clear();
        _runBytes.Clear();
    }

    private TestAttachment RetainOne(
        string runId,
        int index,
        TestAttachment attachment,
        string? sourcePath,
        int retainedForTest,
        long runBytes)
    {
        TestAttachment NotRetained(string reason, long? size = null)
        {
            _log?.Invoke($"Attachment not retained for run {runId}, test {attachment.DisplayName ?? "(unknown)"}: {reason}");
            return attachment with
            {
                Path = null,
                RunId = runId,
                Index = index,
                OriginalPath = sourcePath,
                SizeBytes = size,
                Retained = false,
                NotRetainedReason = reason,
            };
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return NotRetained("Source file is missing.");
        }

        string sourceFilePath;
        try
        {
            sourceFilePath = Path.GetFullPath(sourcePath, _solutionDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NotRetained($"Cannot resolve source file: {ex.Message}");
        }

        if (!File.Exists(sourceFilePath))
        {
            return NotRetained("Source file is missing.");
        }

        long sourceBytes;
        try
        {
            sourceBytes = new FileInfo(sourceFilePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return NotRetained($"Cannot inspect source file: {ex.Message}");
        }

        if (retainedForTest >= MaxAttachmentsPerTest)
        {
            return NotRetained($"Per-test limit of {MaxAttachmentsPerTest} retained files reached.", sourceBytes);
        }

        var remainingRunBytes = MaxRunBytes - runBytes;
        if (sourceBytes > MaxFileBytes)
        {
            return NotRetained($"File exceeds the {MaxFileBytes} byte per-file limit.", sourceBytes);
        }

        if (sourceBytes > remainingRunBytes)
        {
            return NotRetained($"Run exceeds the {MaxRunBytes} byte total artifact limit.", sourceBytes);
        }

        string? destination = null;
        try
        {
            var runDirectory = RunDirectory(runId);
            EnsureSafeDirectory(runDirectory);
            var name = $"{index:D4}-{SafeFileName(Path.GetFileName(sourceFilePath))}";
            destination = Path.Combine(runDirectory, name);
            var copied = CopyBounded(sourceFilePath, destination, MaxFileBytes);
            if (copied is null)
            {
                return NotRetained($"File grew beyond the {MaxFileBytes} byte per-file limit.", sourceBytes);
            }

            if (copied > remainingRunBytes)
            {
                TryDeleteFile(destination);
                return NotRetained($"Run exceeds the {MaxRunBytes} byte total artifact limit.", copied);
            }

            return attachment with
            {
                Path = Path.GetRelativePath(_solutionDirectory, destination),
                RunId = runId,
                Index = index,
                OriginalPath = sourcePath,
                SizeBytes = copied,
                Retained = true,
                NotRetainedReason = null,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or SecurityException)
        {
            if (destination is not null)
            {
                TryDeleteFile(destination);
            }

            return NotRetained($"Cannot copy attachment: {ex.Message}", sourceBytes);
        }
    }

    private string RunDirectory(string runId)
    {
        if (!IsValidRunId(runId))
        {
            throw new ArgumentException("Run ID must be a 32-character hexadecimal value.", nameof(runId));
        }

        return Path.Combine(_rootDirectory, runId);
    }

    private void EnsureSafeDirectory(string runDirectory)
    {
        if (HasReparsePointInPath(_solutionDirectory))
        {
            throw new IOException("Solution directory contains a symbolic link or reparse point.");
        }

        var stateDirectory = Path.Combine(_solutionDirectory, ".piston");
        CreateSafeDirectory(stateDirectory);
        CreateSafeDirectory(_rootDirectory);
        CreateSafeDirectory(runDirectory);
    }

    private static void CreateSafeDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            _ = Directory.CreateDirectory(path);
        }

        if (HasReparsePointInPath(path))
        {
            throw new IOException("Artifact directory contains a symbolic link or reparse point.");
        }
    }

    private long? CopyBounded(string sourcePath, string destinationPath, long maxBytes)
    {
        using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan);
        var buffer = new byte[CopyBufferSize];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, maxBytes - total + 1))) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                destination.Dispose();
                TryDeleteFile(destinationPath);
                return null;
            }

            destination.Write(buffer, 0, read);
        }

        return total;
    }

    private static string SafeFileName(string name)
    {
        var safe = new string(name.Select(ch =>
            char.IsControl(ch) || Path.GetInvalidFileNameChars().Contains(ch) || ch is '/' or '\\'
                ? '_'
                : ch).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "attachment" : safe;
    }

    private static bool IsWithin(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool HasReparsePoint(string path)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    private static bool HasReparsePointInPath(string path)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (HasReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, PathComparison))
            {
                return false;
            }

            current = parent;
        }
    }

    private static bool IsValidRunId(string? runId) =>
        runId is { Length: 32 } && runId.All(Uri.IsHexDigit);

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (!HasReparsePointInPath(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Cannot remove test artifacts at {path}: {ex.Message}");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Cannot remove test artifact {path}: {ex.Message}");
        }
    }
}
