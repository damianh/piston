using Piston.Protocol.Messages;

namespace Piston.Protocol;

public interface IEngineClient : IAsyncDisposable
{
    // Commands
    Task StartAsync(string solutionPath);
    Task ForceRunAsync();
    void Stop();
    Task SetFilterAsync(string? filter);
    Task ClearResultsAsync();

    // State
    StateSnapshotNotification? CurrentSnapshot { get; }

    // Connection state
    ConnectionState ConnectionState { get; }
    event Action<ConnectionState>? ConnectionStateChanged;

    // Events
    event Action<StateSnapshotNotification>? StateChanged;
    event Action<PhaseChangedNotification>? PhaseChanged;
    event Action<TestProgressNotification>? TestProgressChanged;
    event Action<BuildErrorNotification>? BuildError;
}
