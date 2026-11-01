// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

internal sealed class StubBuildService : IBuildService
{
    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken ct) =>
        Task.FromResult(new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));

    public Task<BuildResult> BuildAsync(string solutionPath, IReadOnlyList<string>? projectPaths, CancellationToken ct) =>
        Task.FromResult(new BuildResult(BuildStatus.Succeeded, [], [], TimeSpan.FromMilliseconds(1)));
}
