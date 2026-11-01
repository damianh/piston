// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

internal sealed class ProvenanceBuildService : IBuildService
{
    public bool Fail { get; set; }
    public Action? DuringBuild { get; set; }
    public List<string> Errors { get; } = ["CS0001: boom"];

    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) => BuildAsync(solutionPath, null, ct);

    public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct)
    {
        DuringBuild?.Invoke();
        return Task.FromResult(Fail
            ? new BuildResult(BuildStatus.Failed, Errors, [], TimeSpan.FromMilliseconds(1))
            : new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));
    }
}
