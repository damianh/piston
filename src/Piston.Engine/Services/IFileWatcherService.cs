// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IFileWatcherService : IDisposable
{
    event Action<FileChangeBatch> FileChanged;

    /// <summary>True while changes are being debounced and have not yet been raised as a batch.</summary>
    bool HasPendingChanges => false;
    /// <summary>A latched monitoring failure; verification is unsafe until the watcher is restarted.</summary>
    string? MonitoringError => null;
    void Start(string solutionDirectory);
    void Stop();

    /// <summary>
    /// Limits the watched inputs to <paramref name="scope"/> while it applies to the watched solution directory;
    /// null watches every file there. Persists across <see cref="Start"/> and <see cref="Stop"/>.
    /// </summary>
    void UpdateScope(SourceInputScope? scope) { }
}
