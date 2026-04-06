namespace Piston.Engine.Services;

/// <summary>
/// Lightweight append-only file logger that writes diagnostic entries to
/// <c>.piston/diagnostics.log</c> under the solution directory.
/// Thread-safe. All writes are best-effort — failures are silently swallowed
/// so diagnostics never break the running engine.
/// </summary>
internal sealed class DiagnosticLog : IDisposable
{
    private readonly Lock _lock = new();
    private StreamWriter? _writer;
    private bool _disposed;

    /// <summary>
    /// Shared singleton. Set via <see cref="Initialize"/> during engine startup.
    /// Components use <see cref="Instance"/> to log; if null, logging is silently skipped.
    /// </summary>
    internal static DiagnosticLog? Instance { get; private set; }

    /// <summary>
    /// Initializes the diagnostic log for the given solution directory.
    /// Creates <c>.piston/diagnostics.log</c> (truncated on each engine start).
    /// </summary>
    internal static DiagnosticLog Initialize(string solutionDirectory)
    {
        var pistonDir = Path.Combine(solutionDirectory, ".piston");
        Directory.CreateDirectory(pistonDir);

        var logPath = Path.Combine(pistonDir, "diagnostics.log");
        var log = new DiagnosticLog();

        try
        {
            log._writer = new StreamWriter(logPath, append: false) { AutoFlush = true };
            log.Write("DiagnosticLog", $"Initialized. Log path: {logPath}");
        }
        catch
        {
            // Cannot open log file — run without diagnostics.
        }

        Instance = log;
        return log;
    }

    /// <summary>
    /// Writes a single timestamped log entry.
    /// </summary>
    internal void Write(string category, string message)
    {
        if (_disposed) return;

        try
        {
            lock (_lock)
            {
                _writer?.WriteLine($"[{DateTime.UtcNow:HH:mm:ss.fff}] [{category}] {message}");
            }
        }
        catch
        {
            // Best-effort — never throw from diagnostics.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }

        if (Instance == this)
            Instance = null;
    }
}
