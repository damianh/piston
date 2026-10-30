// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Orchestration;

public sealed partial class PistonOrchestrator
{
    // Retries and backfill run outside the initial-results overlay, but still publish resource contention.
    private async Task<TestRunResult> RunSecondaryTestsAsync(
        string solutionPath,
        IReadOnlyList<string>? targets,
        string? filter,
        bool collectCoverage,
        Action<ProjectTestResult>? onProjectCompleted,
        CancellationToken ct,
        SecondaryResourceState? sharedState = null,
        bool reuseDiscovery = false)
    {
        using var ownedState = sharedState is null ? new SecondaryResourceState(_state) : null;
        var resourceState = sharedState ?? ownedState!;
        using var run = resourceState.StartRun();

        return await _testRunner.RunTestsAsync(solutionPath, targets, filter, collectCoverage, new TestRunCallbacks
        {
            ReuseDiscovery = reuseDiscovery,
            OnProjectWaiting = (project, wait) => run.Update(project, wait),
            OnProjectResourcesAcquired = project => run.Update(project, null, ProjectRunStatus.Pending),
            OnProjectStarted = project => run.Update(project, null, ProjectRunStatus.Running),
            OnProjectCompleted = project =>
            {
                run.Update(project.ProjectPath, null);
                onProjectCompleted?.Invoke(project);
            },
        }, ct).ConfigureAwait(false);
    }

    // Concurrent backfill measurements share this overlay so one cannot erase another's wait
    // or restore a stale status snapshot.
    private sealed class SecondaryResourceState(PistonState state) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly IReadOnlyDictionary<string, ProjectRunStatus> _originalStatuses =
            new Dictionary<string, ProjectRunStatus>(state.ProjectStatuses, StringComparer.Ordinal);
        private readonly IReadOnlyDictionary<string, TestResourceWait> _originalWaits =
            new Dictionary<string, TestResourceWait>(state.ProjectResourceWaits, StringComparer.Ordinal);
        private readonly HashSet<object> _runs = [];
        private readonly Dictionary<(object Run, string Project), ProjectState> _projects = [];
        private bool _disposed;

        public Run StartRun()
        {
            var id = new object();
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _ = _runs.Add(id);
            }

            return new Run(this, id);
        }

        private void Update(object run, string project, TestResourceWait? wait, ProjectRunStatus? status)
        {
            lock (_gate)
            {
                if (!_runs.Contains(run))
                {
                    return;
                }

                if (wait is null && status is null)
                {
                    _ = _projects.Remove((run, project));
                }
                else
                {
                    _projects[(run, project)] = new ProjectState(status ?? ProjectRunStatus.WaitingForResources, wait);
                }

                PublishLocked();
            }

            state.NotifyChanged();
        }

        private void EndRun(object run)
        {
            lock (_gate)
            {
                if (!_runs.Remove(run))
                {
                    return;
                }

                foreach (var key in _projects.Keys.Where(key => ReferenceEquals(key.Run, run)).ToArray())
                {
                    _ = _projects.Remove(key);
                }

                PublishLocked();
            }

            state.NotifyChanged();
        }

        private void PublishLocked()
        {
            var statuses = new Dictionary<string, ProjectRunStatus>(_originalStatuses, StringComparer.Ordinal);
            var waits = new Dictionary<string, TestResourceWait>(_originalWaits, StringComparer.Ordinal);
            foreach (var group in _projects.GroupBy(item => item.Key.Project, StringComparer.Ordinal))
            {
                var wait = group.Select(item => item.Value.Wait).FirstOrDefault(item => item is not null);
                if (wait is not null)
                {
                    waits[group.Key] = wait;
                    statuses[group.Key] = ProjectRunStatus.WaitingForResources;
                }
                else
                {
                    _ = waits.Remove(group.Key);
                    statuses[group.Key] = group.Any(item => item.Value.Status == ProjectRunStatus.Running)
                        ? ProjectRunStatus.Running
                        : ProjectRunStatus.Pending;
                }
            }

            state.ProjectResourceWaits = waits;
            state.ProjectStatuses = statuses;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _runs.Clear();
                _projects.Clear();
                state.ProjectResourceWaits = _originalWaits;
                state.ProjectStatuses = _originalStatuses;
            }

            state.NotifyChanged();
        }

        private sealed record ProjectState(ProjectRunStatus Status, TestResourceWait? Wait);

        public sealed class Run(SecondaryResourceState owner, object id) : IDisposable
        {
            public void Update(string project, TestResourceWait? wait, ProjectRunStatus? status = null) =>
                owner.Update(id, project, wait, status);

            public void Dispose() => owner.EndRun(id);
        }
    }
}
