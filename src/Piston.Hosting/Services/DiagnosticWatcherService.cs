using Piston.Engine;
using Piston.Protocol.Messages;
using Piston.Roslyn;
using Piston.Roslyn.Messages;

namespace Piston.Hosting.Services;

/// <summary>
/// Periodically polls <see cref="IRoslynWorkspace"/> for diagnostics, diffs against the
/// previous snapshot, and emits <see cref="ActivityEventTypes.DiagnosticsChanged"/> events.
/// Also maintains <see cref="CurrentDiagnostics"/> for on-demand tab fetch.
/// </summary>
public sealed class DiagnosticWatcherService : IDisposable
{
    private static readonly TimeSpan PollingInterval = TimeSpan.FromSeconds(5);

    private readonly IRoslynWorkspace _workspace;
    private readonly IActivityEventSink _activitySink;
    private readonly string? _solutionPath;
    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<DiagnosticResult> _snapshot = [];
    private IReadOnlyList<DiagnosticEntryData> _snapshotMapped = [];

    public IReadOnlyList<DiagnosticEntryData> CurrentDiagnostics => _snapshotMapped;

    public DiagnosticWatcherService(
        IRoslynWorkspace workspace,
        IActivityEventSink activitySink,
        string? solutionPath)
    {
        _workspace    = workspace;
        _activitySink = activitySink;
        _solutionPath = solutionPath;
    }

    /// <summary>Starts the polling loop in the background.</summary>
    public void Start() =>
        _ = Task.Run(() => PollLoopAsync(_cts.Token));

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PollingInterval, ct).ConfigureAwait(false);
                await PollOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Polling errors are non-fatal — retry on next interval
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken ct)
    {
        var response = await _workspace.GetDiagnosticsAsync(null, ct).ConfigureAwait(false);
        var current = response.Diagnostics;

        var prev = _snapshot;
        if (AreSame(prev, current))
            return;

        _snapshot = current;
        _snapshotMapped = current.Select(ToEntryData).ToList();

        var prevSet  = prev.ToHashSet(DiagnosticKeyComparer.Instance);
        var currSet  = current.ToHashSet(DiagnosticKeyComparer.Instance);
        var added    = current.Where(d => !prevSet.Contains(d)).Select(ToEntryData).ToList();
        var removed  = prev.Where(d => !currSet.Contains(d)).Select(ToEntryData).ToList();

        if (added.Count > 0 || removed.Count > 0)
        {
            _activitySink.Emit(ActivityEventFactory.DiagnosticsChanged(_solutionPath, added, removed));
        }
    }

    private static bool AreSame(
        IReadOnlyList<DiagnosticResult> a,
        IReadOnlyList<DiagnosticResult> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!DiagnosticKeyComparer.Instance.Equals(a[i], b[i])) return false;
        }
        return true;
    }

    private static DiagnosticEntryData ToEntryData(DiagnosticResult d) =>
        new(d.Severity, d.Id, d.Message, d.FilePath, d.Line, d.Column, null);

    private sealed class DiagnosticKeyComparer : IEqualityComparer<DiagnosticResult>
    {
        public static readonly DiagnosticKeyComparer Instance = new();

        public bool Equals(DiagnosticResult? x, DiagnosticResult? y)
        {
            if (x is null && y is null) return true;
            if (x is null || y is null) return false;
            return x.Id == y.Id
                && x.Message == y.Message
                && x.FilePath == y.FilePath
                && x.Line == y.Line
                && x.Severity == y.Severity;
        }

        public int GetHashCode(DiagnosticResult d) =>
            HashCode.Combine(d.Id, d.Message, d.FilePath, d.Line, d.Severity);
    }
}
