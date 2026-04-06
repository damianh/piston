using Piston.Engine.Impact;

namespace Piston.Engine.Tests.Impact;

internal sealed class StubSolutionGraph : ISolutionGraph
{
    private readonly List<string> _allProjects;
    private readonly List<string> _testProjects;
    private readonly Dictionary<string, IReadOnlySet<string>> _transitiveDependents;
    private readonly Dictionary<string, string> _fileToProject;
    private readonly Dictionary<string, IReadOnlyList<string>> _projectToFiles;
    private readonly HashSet<string> _mtpProjects;
    private readonly Dictionary<string, string> _mtpOutputPaths;

    public StubSolutionGraph(
        IEnumerable<string> allProjects,
        IEnumerable<string> testProjects,
        Dictionary<string, IReadOnlySet<string>>? transitiveDependents = null,
        Dictionary<string, string>? fileToProject = null,
        Dictionary<string, IReadOnlyList<string>>? projectToFiles = null,
        HashSet<string>? mtpProjects = null,
        Dictionary<string, string>? mtpOutputPaths = null)
    {
        _allProjects = allProjects.ToList();
        _testProjects = testProjects.ToList();
        _transitiveDependents = transitiveDependents ?? [];
        _fileToProject = fileToProject ?? [];
        _projectToFiles = projectToFiles ?? [];
        _mtpProjects = mtpProjects ?? [];
        _mtpOutputPaths = mtpOutputPaths ?? [];
    }

    public IReadOnlyList<string> AllProjectPaths => _allProjects;
    public IReadOnlyList<string> TestProjectPaths => _testProjects;

    public string? FindOwningProject(string filePath) =>
        _fileToProject.TryGetValue(filePath, out var proj) ? proj : null;

    public IReadOnlySet<string> GetTransitiveDependents(string projectPath) =>
        _transitiveDependents.TryGetValue(projectPath, out var deps)
            ? deps
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> GetSourceFiles(string projectPath) =>
        _projectToFiles.TryGetValue(projectPath, out var files) ? files : [];

    public bool IsTestProject(string projectPath) =>
        _testProjects.Contains(projectPath, StringComparer.OrdinalIgnoreCase);

    public bool IsMtpProject(string projectPath) =>
        _mtpProjects.Contains(projectPath, StringComparer.OrdinalIgnoreCase);

    public string? GetMtpOutputPath(string projectPath) =>
        _mtpOutputPaths.TryGetValue(projectPath, out var p) ? p : null;
}
