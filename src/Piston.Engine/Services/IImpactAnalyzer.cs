// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IImpactAnalyzer
{
    Task InitializeAsync(string solutionPath, CancellationToken ct);
    ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes);
    ImpactAnalysisResult AnalyzeFullRun();
    void InvalidateGraph();
    IReadOnlyList<string> GetAllTestProjectPaths();
    bool IsMtpProject(string projectPath);
    string? GetMtpOutputPath(string projectPath);

    /// <summary>
    /// Projects a build of <paramref name="buildTargets"/> compiles: the targets and their references,
    /// or every project in the solution when <paramref name="buildTargets"/> is null or empty.
    /// Empty when the solution graph is unavailable.
    /// </summary>
    IReadOnlyList<string> GetBuildProjectPaths(IReadOnlyList<string>? buildTargets) => buildTargets ?? [];

    /// <summary>
    /// Refreshes the graph used for run selection. Returns null only when evaluated project inputs
    /// are covered by the source fingerprint.
    /// </summary>
    string? GetVerificationCoverageIssue(string solutionPath) => "Build-input coverage is unavailable.";

    /// <summary>Evaluates coverage and captures the refreshed graph's test projects together.</summary>
    string? GetVerificationCoverageIssue(string solutionPath, out IReadOnlyList<string> testProjects)
    {
        var issue = GetVerificationCoverageIssue(solutionPath);
        testProjects = GetAllTestProjectPaths().ToArray();
        return issue;
    }
}
