// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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

    private readonly Lock _lock = new();
    private readonly string _solutionDirectory;
    private List<string>? _buffer = [];
    private StreamWriter? _writer;
    private bool _disposed;

    private DiagnosticLog(string solutionDirectory) => _solutionDirectory = solutionDirectory;

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
    internal static DiagnosticLog Initialize(string solutionDirectory)
    {
        var log = new DiagnosticLog(solutionDirectory);
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

                // FileShare.Delete so an open log never blocks removing the worktree (Windows).
                var stream = new FileStream(
                    logPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                _writer = new StreamWriter(stream) { AutoFlush = true };
                FilePath = logPath;
                _writer.WriteLine(Format("DiagnosticLog", $"Initialized. Log path: {logPath}"));
                foreach (var line in buffered)
                {
                    _writer.WriteLine(line);
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

                _writer?.WriteLine(line);
            }
        }
        catch
        {
            // Best-effort — never throw from diagnostics.
        }
    }

    private static string Format(string category, string message) =>
        $"[{DateTime.UtcNow:HH:mm:ss.fff}] [{category}] {message}";

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
