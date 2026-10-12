// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IFileWatcherService : IDisposable
{
    event Action<FileChangeBatch> FileChanged;

    /// <summary>True while changes are being debounced and have not yet been raised as a batch.</summary>
    bool HasPendingChanges => false;
    void Start(string solutionDirectory);
    void Stop();
}
