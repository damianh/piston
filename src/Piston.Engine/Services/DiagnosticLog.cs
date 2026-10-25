// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Lightweight append-only file logger that writes diagnostic entries to
/// <c>.piston/diagnostics.log</c> under the solution directory.
/// Entries are buffered in memory until <see cref="Open"/> is called, which the engine does only once it
/// owns the worktree, so a process refused ownership never touches the live owner's log.
/// Thread-safe. All writes are best-effort — failures are silently swallowed
/// so diagnostics never break the running engine.
/// </summary>
internal sealed class DiagnosticLog : IDisposable
{
    private const int MaxBufferedEntries = 1000;
    internal const long DefaultMaxFileBytes = 10 * 1024 * 1024;

    private readonly Lock _lock = new();
    private readonly string _solutionDirectory;
    private readonly long _maxFileBytes;
    private long _bytesWritten;
    private List<string>? _buffer = [];
    private StreamWriter? _writer;
    private bool _disposed;

    internal DiagnosticLog(string solutionDirectory, long maxFileBytes = DefaultMaxFileBytes)
    {
        _solutionDirectory = solutionDirectory;
        _maxFileBytes = Math.Max(1, maxFileBytes);
    }

    /// <summary>
    /// Shared singleton. Set via <see cref="Initialize"/> during engine startup.
    /// Components use <see cref="Instance"/> to log; if null, logging is silently skipped.
    /// </summary>
    internal static DiagnosticLog? Instance { get; private set; }

    /// <summary>Path of the log file once <see cref="Open"/> has succeeded; otherwise null.</summary>
    internal string? FilePath { get; private set; }

    /// <summary>
    /// Creates the diagnostic log for the given solution directory without touching the file system.
    /// Entries are buffered until <see cref="Open"/>.
    /// </summary>
    internal static DiagnosticLog Initialize(string solutionDirectory, long maxFileBytes = DefaultMaxFileBytes)
    {
        var log = new DiagnosticLog(solutionDirectory, maxFileBytes);
        Instance = log;
        return log;
    }

    /// <summary>
    /// Creates <c>.piston/diagnostics.log</c> (truncated) and flushes buffered entries.
    /// Call only while holding worktree ownership. Idempotent.
    /// </summary>
    internal void Open()
    {
        lock (_lock)
        {
            if (_disposed || _buffer is null)
            {
                return;
            }

            var buffered = _buffer;
            _buffer = null;
            try
            {
                var pistonDir = Path.Combine(_solutionDirectory, ".piston");
                _ = Directory.CreateDirectory(pistonDir);
                var logPath = Path.Combine(pistonDir, "diagnostics.log");

                File.Delete(logPath + ".1");
                _writer = OpenWriter(logPath, FileMode.Create);
                _bytesWritten = 0;
                FilePath = logPath;
                WriteLineLocked(Format("DiagnosticLog", $"Initialized. Log path: {logPath}"));
                foreach (var line in buffered)
                {
                    WriteLineLocked(line);
                }
            }
            catch
            {
                // Cannot open log file — run without diagnostics.
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>
    /// Writes a single timestamped log entry.
    /// </summary>
    internal void Write(string category, string message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            lock (_lock)
            {
                var line = Format(category, message);
                if (_buffer is not null)
                {
                    if (_buffer.Count < MaxBufferedEntries)
                    {
                        _buffer.Add(line);
                    }
                    return;
                }

                WriteLineLocked(line);
            }
        }
        catch
        {
            // Best-effort — never throw from diagnostics.
        }
    }

    private void WriteLineLocked(string line)
    {
        if (_writer is null)
        {
            return;
        }

        line = RetentionLimits.TruncateHead(line, RetentionLimits.MaxDiagnosticLogLineLength)!;
        var lineBytes = Encoding.UTF8.GetByteCount(line) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        if (_bytesWritten > 0 && _bytesWritten + lineBytes > _maxFileBytes)
        {
            RotateLocked();
        }

        if (_writer is not null)
        {
            _writer.WriteLine(line);
            _bytesWritten += lineBytes;
        }
    }

    private void RotateLocked()
    {
        if (FilePath is null || _writer is null)
        {
            return;
        }

        _writer.Flush();
        _writer.Dispose();
        _writer = null;
        try
        {
            File.Delete(FilePath + ".1");
            File.Move(FilePath, FilePath + ".1");
            _writer = OpenWriter(FilePath, FileMode.Create);
            _bytesWritten = 0;
            var rotatedLine = Format("DiagnosticLog", "Rotated diagnostics.log; previous log is diagnostics.log.1");
            _writer.WriteLine(rotatedLine);
            _bytesWritten = Encoding.UTF8.GetByteCount(rotatedLine) + Encoding.UTF8.GetByteCount(Environment.NewLine);
        }
        catch
        {
            _writer?.Dispose();
            _writer = null;
            try
            {
                _writer = OpenWriter(FilePath, FileMode.Append);
                _bytesWritten = new FileInfo(FilePath).Length;
            }
            catch
            {
                _writer = null;
                throw;
            }
        }
    }

    private static StreamWriter OpenWriter(string path, FileMode mode)
    {
        var stream = new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        return new StreamWriter(stream) { AutoFlush = true };
    }

    private static string Format(string category, string message) =>
        RetentionLimits.TruncateHead($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{category}] {message}",
            RetentionLimits.MaxDiagnosticLogLineLength)!;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_lock)
        {
            _buffer = null;
            _writer?.Dispose();
            _writer = null;
        }

        if (Instance == this)
        {
            Instance = null;
        }
    }
}
