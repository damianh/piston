// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>
/// Collects the <see cref="OwnedProcess"/>es started within an async flow, such as one project's test execution,
/// so hang diagnostics can find the test host processes to inspect.
/// </summary>
internal sealed class OwnedProcessScope : IDisposable
{
    private static readonly AsyncLocal<OwnedProcessScope?> CurrentScope = new();

    private readonly OwnedProcessScope? _previous;
    private readonly Lock _gate = new();
    private readonly List<OwnedProcess> _processes = [];

    private OwnedProcessScope() => _previous = CurrentScope.Value;

    /// <summary>The scope of the current async flow, if any.</summary>
    public static OwnedProcessScope? Current => CurrentScope.Value;

    /// <summary>Starts a scope for the current async flow; processes started in it are tracked until it is disposed.</summary>
    public static OwnedProcessScope Begin()
    {
        var scope = new OwnedProcessScope();
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>The tracked processes that have not been disposed.</summary>
    public IReadOnlyList<OwnedProcess> Processes
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes];
            }
        }
    }

    /// <summary>Tracks <paramref name="process"/> in the current scope, if any.</summary>
    public static void TrackInCurrent(OwnedProcess process) => Current?.Track(process);

    public void Track(OwnedProcess process)
    {
        lock (_gate)
        {
            if (!_processes.Contains(process))
            {
                _processes.Add(process);
                process.Disposed += Untrack;
            }
        }
    }

    private void Untrack(OwnedProcess process)
    {
        lock (_gate)
        {
            _ = _processes.Remove(process);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var process in _processes)
            {
                process.Disposed -= Untrack;
            }

            _processes.Clear();
        }

        if (CurrentScope.Value == this)
        {
            CurrentScope.Value = _previous;
        }
    }
}
