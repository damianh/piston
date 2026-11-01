// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

// ── Stubs ─────────────────────────────────────────────────────────────────────

internal sealed class StubFileWatcherService : IFileWatcherService
{
    public event Action<FileChangeBatch>? FileChanged;

    public void Start(string directory) { }
    public void Stop() { }
    public void Dispose() { }

    // Allow tests to trigger file changes
    public void TriggerChange(FileChangeBatch batch) => FileChanged?.Invoke(batch);
}
