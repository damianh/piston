namespace Piston.Engine.Impact;

internal interface ISolutionGraph
{
    IReadOnlyList<string> AllProjectPaths { get; }
    IReadOnlyList<string> TestProjectPaths { get; }
    string? FindOwningProject(string filePath);
    IReadOnlySet<string> GetTransitiveDependents(string projectPath);
    IReadOnlyList<string> GetSourceFiles(string projectPath);
    bool IsTestProject(string projectPath);
    bool IsMtpProject(string projectPath);
    string? GetMtpOutputPath(string projectPath);
}
