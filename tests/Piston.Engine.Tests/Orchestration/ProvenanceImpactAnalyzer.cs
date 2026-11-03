// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;
using Piston.Engine.Services;

namespace Piston.Engine.Tests.Orchestration;

internal sealed class ProvenanceImpactAnalyzer(IReadOnlyList<string> testProjects) : IImpactAnalyzer
{
    public IReadOnlyList<string> TestProjects { get; set; } = testProjects;

    /// <summary>The test projects the graph finds once reloaded after restore; null when it was restored already.</summary>
    public IReadOnlyList<string>? TestProjectsAfterRestore { get; set; }
    public bool ProjectDiscoveryComplete { get; set; } = true;

    public IReadOnlyList<string>? GetRestoredTestProjectPaths() =>
        ProjectDiscoveryComplete && TestProjectsAfterRestore is null ? TestProjects : null;

    public bool RefreshGraphIfEvaluatedBeforeRestore()
    {
        if (TestProjectsAfterRestore is not { } restored)
        {
            return false;
        }

        TestProjects = restored;
        TestProjectsAfterRestore = null;
        return true;
    }

    public Func<string?> VerificationCoverage { get; set; } = () => null;
    public int TargetFrameworkCount { get; set; } = 1;
    public string? GetVerificationCoverageIssue(string solutionPath) => VerificationCoverage();
    public int GetTargetFrameworkCount(string projectPath) => TargetFrameworkCount;
    public Task Initialization { get; set; } = Task.CompletedTask;
    public HashSet<string> MtpProjects { get; } = [];
    public Task InitializeAsync(string solutionPath, CancellationToken ct) => Initialization;

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes) =>
        new([TestProjects[0]], [TestProjects[0]], RequiresGraphRebuild: false, IsFullRun: false);

    public ImpactAnalysisResult AnalyzeFullRun() =>
        new(TestProjects, TestProjects, RequiresGraphRebuild: false, IsFullRun: true);

    public void InvalidateGraph() { }
    public IReadOnlyList<string> GetAllTestProjectPaths() => TestProjects;
    public bool IsMtpProject(string projectPath) => MtpProjects.Contains(projectPath);
    public string? GetMtpOutputPath(string projectPath) => IsMtpProject(projectPath) ? projectPath + ".dll" : null;
}
