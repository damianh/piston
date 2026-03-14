using Piston.Engine.Models;

namespace Piston.Engine.Services;

public interface IImpactAnalyzer
{
    Task InitializeAsync(string solutionPath, CancellationToken ct);
    ImpactAnalysisResult Analyze(IReadOnlyList<FileChangeEvent> changes);
    ImpactAnalysisResult AnalyzeFullRun();
    void InvalidateGraph();
}
