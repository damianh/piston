using Piston.Controller.Mapping;
using Piston.Engine;
using Piston.Protocol;
using Piston.Protocol.Messages;

namespace Piston.Controller;

/// <summary>
/// In-process <see cref="IEngineClient"/> implementation that hosts a <see cref="PistonEngine"/>
/// directly — no transport, no serialization. Used for embedded (single-process) mode.
/// </summary>
internal sealed class EmbeddedEngineClient : IEngineClient
{
    private readonly IEngine _engine;
    private StateSnapshotNotification? _currentSnapshot;
    private readonly Lock _snapshotLock = new();

    public event Action<StateSnapshotNotification>? StateChanged;
    public event Action<PhaseChangedNotification>? PhaseChanged;
    public event Action<TestProgressNotification>? TestProgressChanged;
    public event Action<BuildErrorNotification>? BuildError;

    // EmbeddedEngineClient is always connected (no transport layer)
    public ConnectionState ConnectionState => ConnectionState.Connected;
    public event Action<ConnectionState>? ConnectionStateChanged { add { } remove { } }

    public StateSnapshotNotification? CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
                return _currentSnapshot;
        }
    }

    internal EmbeddedEngineClient(PistonOptions options)
    {
        _engine = new PistonEngine(options);
        _engine.State.StateChanged += OnEngineStateChanged;
    }

    private void OnEngineStateChanged()
    {
        StateSnapshotNotification snapshot;
        lock (_snapshotLock)
        {
            snapshot = _engine.State.ToSnapshot();
            _currentSnapshot = snapshot;
        }

        StateChanged?.Invoke(snapshot);
        PhaseChanged?.Invoke(new PhaseChangedNotification(snapshot.Phase, null));

        if (snapshot.Phase == Piston.Protocol.Dtos.PistonPhaseDto.Testing)
        {
            TestProgressChanged?.Invoke(new TestProgressNotification(
                snapshot.InProgressSuites,
                snapshot.CompletedTests,
                snapshot.TotalExpectedTests));
        }

        if (snapshot.Phase == Piston.Protocol.Dtos.PistonPhaseDto.Error && snapshot.LastBuild is not null)
        {
            BuildError?.Invoke(new BuildErrorNotification(snapshot.LastBuild));
        }
    }

    public Task StartAsync(string solutionPath) => _engine.StartAsync(solutionPath);

    public Task ForceRunAsync()
    {
        _ = _engine.ForceRunAsync();
        return Task.CompletedTask;
    }

    public void Stop() => _engine.Stop();

    public Task SetFilterAsync(string? filter)
    {
        _engine.SetFilter(filter);
        return Task.CompletedTask;
    }

    public Task ClearResultsAsync()
    {
        _engine.ClearResults();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _engine.State.StateChanged -= OnEngineStateChanged;
        _engine.Dispose();
        return ValueTask.CompletedTask;
    }
}
