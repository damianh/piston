// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Piston.Engine.Models;

namespace Piston.Engine.History;

internal sealed class SavedResultsStore : IDisposable
{
    private const int FormatVersion = 1;
    private const int MaxOutputLength = 4096;
    private const int MaxErrorLength = 8192;
    private const int MaxBuildDiagnostics = 100;
    private const int MaxBuildDiagnosticLength = 2048;
    internal TimeSpan SaveDelay { get; set; } = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly string _solutionPath;
    private readonly string _filePath;
    private readonly string _engineVersion;
    private readonly Action<string> _log;
    private readonly Timer _timer;
    private SavedResultsSnapshot? _pending;
    private bool _disposed;

    public SavedResultsStore(string solutionPath, Action<string> log)
    {
        _solutionPath = Path.GetFullPath(solutionPath);
        _filePath = GetFilePath(_solutionPath);
        _engineVersion = typeof(SavedResultsStore).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(SavedResultsStore).Assembly.GetName().Version?.ToString()
            ?? "unknown";
        _log = log;
        _timer = new Timer(_ => Save(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public SavedResultsSnapshot? TryLoad()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var file = JsonSerializer.Deserialize<SavedResultsFile>(stream, JsonOptions);
            if (file is null)
            {
                _log($"Ignoring saved results {_filePath}: file is empty.");
                return null;
            }

            if (file.FormatVersion != FormatVersion)
            {
                _log($"Ignoring saved results {_filePath}: unsupported format version {file.FormatVersion}.");
                return null;
            }

            if (!string.Equals(file.EngineVersion, _engineVersion, StringComparison.Ordinal))
            {
                _log($"Ignoring saved results {_filePath}: engine version '{file.EngineVersion}' does not match '{_engineVersion}'.");
                return null;
            }

            if (!PathsEqual(file.SolutionPath, _solutionPath))
            {
                _log($"Ignoring saved results {_filePath}: solution path does not match '{_solutionPath}'.");
                return null;
            }

            if (file.TestSuites is null)
            {
                _log($"Ignoring saved results {_filePath}: test suites are missing.");
                return null;
            }

            _log($"Loaded saved results for {file.TestSuites.Sum(s => s.Tests.Count)} test(s) from {_filePath}.");
            return new SavedResultsSnapshot(
                file.SavedAt,
                file.LastRunTime,
                file.LastBuildDuration,
                file.LastTestDuration,
                file.LastBuild,
                file.TestSuites,
                file.LastRun);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Cannot read saved results {_filePath}: {ex.GetType().Name}: {ex.Message}; starting without saved results.");
            return null;
        }
    }

    public void Schedule(SavedResultsSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = Clip(snapshot);
            _ = _timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Clear()
    {
        lock (_saveGate)
        {
            lock (_gate)
            {
                _pending = null;
                _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            DeleteFile(_filePath, _log);
        }
    }

    public static void Delete(string solutionPath, Action<string> log) =>
        DeleteFile(GetFilePath(Path.GetFullPath(solutionPath)), log);

    public void Dispose()
    {
        lock (_saveGate)
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer.Dispose();
            }

            WritePending();
        }
    }

    private void Save()
    {
        lock (_saveGate)
        {
            WritePending();
        }
    }

    private void WritePending()
    {
        SavedResultsSnapshot? snapshot;
        lock (_gate)
        {
            snapshot = _pending;
            _pending = null;
        }

        if (snapshot is null)
        {
            return;
        }

        var file = new SavedResultsFile
        {
            FormatVersion = FormatVersion,
            EngineVersion = _engineVersion,
            SolutionPath = _solutionPath,
            SavedAt = DateTimeOffset.UtcNow,
            LastRunTime = snapshot.LastRunTime,
            LastBuildDuration = snapshot.LastBuildDuration,
            LastTestDuration = snapshot.LastTestDuration,
            LastBuild = snapshot.LastBuild,
            TestSuites = snapshot.TestSuites,
            LastRun = snapshot.LastRun,
        };
        var temporaryPath = _filePath + ".tmp";
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file, JsonOptions);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log($"Cannot save results {_filePath}: {ex.GetType().Name}: {ex.Message}.");
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                _log($"Cannot remove temporary saved results {temporaryPath}: {cleanup.Message}.");
            }
        }
    }

    private static SavedResultsSnapshot Clip(SavedResultsSnapshot snapshot)
    {
        var suites = snapshot.TestSuites.Select(Clip).ToList();
        var lastRun = snapshot.LastRun is null
            ? null
            : snapshot.LastRun with
            {
                RunnerError = ClipText(snapshot.LastRun.RunnerError, MaxErrorLength),
                Build = Clip(snapshot.LastRun.Build),
                Suites = snapshot.LastRun.Suites.Select(Clip).ToList(),
            };

        return snapshot with
        {
            LastBuild = Clip(snapshot.LastBuild),
            TestSuites = suites,
            LastRun = lastRun,
        };
    }

    private static TestSuite Clip(TestSuite suite) =>
        suite with { Tests = suite.Tests.Select(Clip).ToList(), ExecutionLog = null };

    private static TestResult Clip(TestResult result) =>
        result with
        {
            Output = ClipText(result.Output, MaxOutputLength),
            ErrorMessage = ClipText(result.ErrorMessage, MaxErrorLength),
            StackTrace = ClipText(result.StackTrace, MaxErrorLength),
        };

    private static BuildResult? Clip(BuildResult? result) => result is null
        ? null
        : result with
        {
            Errors = ClipDiagnostics(result.Errors),
            Warnings = ClipDiagnostics(result.Warnings),
        };

    private static IReadOnlyList<string> ClipDiagnostics(IReadOnlyList<string> diagnostics)
    {
        var keepCount = Math.Max(0, MaxBuildDiagnostics - (diagnostics.Count > MaxBuildDiagnostics ? 1 : 0));
        var kept = diagnostics.Take(keepCount)
            .Select(d => ClipText(d, MaxBuildDiagnosticLength)!)
            .ToList();
        if (diagnostics.Count > keepCount)
        {
            kept.Add($"{diagnostics.Count - keepCount} more omitted");
        }

        return kept;
    }

    private static string? ClipText(string? value, int max)
    {
        const string marker = " [truncated]";
        return value is { Length: > 0 } && value.Length > max
            ? value[..(max - marker.Length)] + marker
            : value;
    }

    private static string GetFilePath(string solutionPath) =>
        Path.Combine(Path.GetDirectoryName(solutionPath)!, ".piston", Path.GetFileNameWithoutExtension(solutionPath) + ".results.json");

    private static bool PathsEqual(string? left, string right) =>
        left is not null && string.Equals(Path.GetFullPath(left), right,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static void DeleteFile(string filePath, Action<string> log)
    {
        try
        {
            File.Delete(filePath);
            File.Delete(filePath + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Cannot delete saved results {filePath}: {ex.Message}.");
        }
    }

    private sealed class SavedResultsFile
    {
        public int FormatVersion { get; set; }
        public string? EngineVersion { get; set; }
        public string? SolutionPath { get; set; }
        public DateTimeOffset SavedAt { get; set; }
        public DateTimeOffset? LastRunTime { get; set; }
        public TimeSpan? LastBuildDuration { get; set; }
        public TimeSpan? LastTestDuration { get; set; }
        public BuildResult? LastBuild { get; set; }
        public IReadOnlyList<TestSuite>? TestSuites { get; set; }
        public TestRunRecord? LastRun { get; set; }
    }
}

internal sealed record SavedResultsSnapshot(
    DateTimeOffset SavedAt,
    DateTimeOffset? LastRunTime,
    TimeSpan? LastBuildDuration,
    TimeSpan? LastTestDuration,
    BuildResult? LastBuild,
    IReadOnlyList<TestSuite> TestSuites,
    TestRunRecord? LastRun);
