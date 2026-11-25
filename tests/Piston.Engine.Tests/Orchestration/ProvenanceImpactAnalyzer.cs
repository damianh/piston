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

    /// <summary>The file paths of every <see cref="Analyze"/> call, in call order.</summary>
    public List<IReadOnlyList<string>> AnalyzedFiles { get; } = [];

    public ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes)
    {
        lock (AnalyzedFiles)
        {
            AnalyzedFiles.Add([.. changes.Select(c => c.FilePath)]);
        }

        return new([TestProjects[0]], [TestProjects[0]], RequiresGraphRebuild: false, IsFullRun: false);
    }

    /// <summary>What <see cref="Preview"/> returns; by default the first test project, like <see cref="Analyze"/>.</summary>
    public ImpactAnalysisResult? PreviewResult { get; set; }

    /// <summary>Called at the start of every <see cref="Preview"/>, e.g. to start a run just before one is requested.</summary>
    public Action? OnPreview { get; set; }

    public ImpactAnalysisResult Preview(IReadOnlyList<FileChangeEvent> changes)
    {
        OnPreview?.Invoke();
        return PreviewResult ?? new([TestProjects[0]], [TestProjects[0]], RequiresGraphRebuild: false, IsFullRun: false);
    }

    public ImpactAnalysisResult AnalyzeFullRun() =>
        new(TestProjects, TestProjects, RequiresGraphRebuild: false, IsFullRun: true);

    public void InvalidateGraph() { }
    public IReadOnlyList<string> GetAllTestProjectPaths() => TestProjects;
    public bool IsMtpProject(string projectPath) => MtpProjects.Contains(projectPath);
    public string? GetMtpOutputPath(string projectPath) => IsMtpProject(projectPath) ? projectPath + ".dll" : null;
}
