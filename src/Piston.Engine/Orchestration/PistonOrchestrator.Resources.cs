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
        CancellationToken ct)
    {
        var resourceStateLock = new Lock();
        var originalStatuses = _state.ProjectStatuses;
        var projectStatuses = new Dictionary<string, ProjectRunStatus>(originalStatuses, StringComparer.Ordinal);
        var resourceWaits = new Dictionary<string, TestResourceWait>(StringComparer.Ordinal);
        var finished = false;

        void UpdateResourceState(string project, TestResourceWait? wait, ProjectRunStatus? status = null)
        {
            lock (resourceStateLock)
            {
                if (finished)
                {
                    return;
                }

                if (wait is not null)
                {
                    resourceWaits[project] = wait;
                    projectStatuses[project] = ProjectRunStatus.WaitingForResources;
                }
                else
                {
                    _ = resourceWaits.Remove(project);
                    if (status is { } current)
                    {
                        projectStatuses[project] = current;
                    }
                    else if (originalStatuses.TryGetValue(project, out var original))
                    {
                        projectStatuses[project] = original;
                    }
                    else
                    {
                        _ = projectStatuses.Remove(project);
                    }
                }

                _state.ProjectResourceWaits = new Dictionary<string, TestResourceWait>(resourceWaits);
                _state.ProjectStatuses = new Dictionary<string, ProjectRunStatus>(projectStatuses);
            }

            _state.NotifyChanged();
        }

        try
        {
            return await _testRunner.RunTestsAsync(solutionPath, targets, filter, collectCoverage, new TestRunCallbacks
            {
                OnProjectWaiting = (project, wait) => UpdateResourceState(project, wait),
                OnProjectResourcesAcquired = project => UpdateResourceState(project, null, ProjectRunStatus.Pending),
                OnProjectStarted = project => UpdateResourceState(project, null, ProjectRunStatus.Running),
                OnProjectCompleted = project =>
                {
                    UpdateResourceState(project.ProjectPath, null);
                    onProjectCompleted?.Invoke(project);
                },
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (resourceStateLock)
            {
                finished = true;
                _state.ProjectResourceWaits = new Dictionary<string, TestResourceWait>();
                _state.ProjectStatuses = originalStatuses;
            }

            _state.NotifyChanged();
        }
    }
}
