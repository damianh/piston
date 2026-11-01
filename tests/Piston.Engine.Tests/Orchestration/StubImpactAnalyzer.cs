// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

internal sealed class StubImpactAnalyzer(IReadOnlyList<string> testProjects) : IImpactAnalyzer
{
    public Task InitializeAsync(string solutionPath, CancellationToken ct) => Task.CompletedTask;

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) =>
        new(testProjects, testProjects, RequiresGraphRebuild: false, IsFullRun: false);

    public ImpactAnalysisResult AnalyzeFullRun() =>
        new(testProjects, testProjects, RequiresGraphRebuild: false, IsFullRun: false);

    public void InvalidateGraph() { }
    public IReadOnlyList<string> GetAllTestProjectPaths() => testProjects;
    public bool IsMtpProject(string projectPath) => false;
    public string? GetMtpOutputPath(string projectPath) => null;
}
