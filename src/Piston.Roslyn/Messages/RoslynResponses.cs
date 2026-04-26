namespace Piston.Roslyn.Messages;

public sealed record WorkspaceInfo(
    string SolutionPath,
    IReadOnlyList<ProjectInfo> Projects);

public sealed record ProjectInfo(
    string Name,
    string FilePath,
    int DocumentCount);
