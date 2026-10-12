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

    /// <summary>Null only when evaluated project inputs are covered by the source fingerprint.</summary>
    string? GetVerificationCoverageIssue(string solutionPath) => "Build-input coverage is unavailable.";
}
