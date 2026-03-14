using Piston.Engine.Impact;

namespace Piston.Engine.Tests.Impact;

internal sealed class StubSolutionGraph : ISolutionGraph
{
    private readonly List<string> _allProjects;
    private readonly List<string> _testProjects;
    private readonly Dictionary<string, IReadOnlySet<string>> _transitiveDependents;
    private readonly Dictionary<string, string> _fileToProject;
    private readonly Dictionary<string, IReadOnlyList<string>> _projectToFiles;

    public StubSolutionGraph(
        IEnumerable<string> allProjects,
        IEnumerable<string> testProjects,
        Dictionary<string, IReadOnlySet<string>>? transitiveDependents = null,
        Dictionary<string, string>? fileToProject = null,
        Dictionary<string, IReadOnlyList<string>>? projectToFiles = null)
    {
        _allProjects = allProjects.ToList();
        _testProjects = testProjects.ToList();
        _transitiveDependents = transitiveDependents ?? [];
        _fileToProject = fileToProject ?? [];
        _projectToFiles = projectToFiles ?? [];
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
}
