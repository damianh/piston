using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;

namespace Piston.Engine.Impact;

internal sealed class MsBuildSolutionGraph : ISolutionGraph
{
    private readonly List<string> _allProjects;
    private readonly List<string> _testProjects;
    private readonly Dictionary<string, ProjectGraphNode> _nodeByPath;
    private readonly Dictionary<string, IReadOnlyList<string>> _sourceFilesByProject;

    // Sorted list of (projectDirectory, projectPath) for directory-based ownership lookup
    private readonly List<(string Directory, string ProjectPath)> _sortedProjectDirs;

    public IReadOnlyList<string> AllProjectPaths => _allProjects;
    public IReadOnlyList<string> TestProjectPaths => _testProjects;

    public MsBuildSolutionGraph(string solutionPath)
    {
        // MsBuildLocatorGuard.EnsureRegistered() MUST have been called before this
        // constructor is executed (i.e. before this class's assembly is JIT-loaded).
        // The caller (PistonEngine or test setup) is responsible for doing so.

        using var collection = new ProjectCollection();

        var graph = new ProjectGraph(solutionPath, collection);

        _allProjects = [];
        _testProjects = [];
        _nodeByPath = new Dictionary<string, ProjectGraphNode>(StringComparer.OrdinalIgnoreCase);
        _sourceFilesByProject = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        _sortedProjectDirs = [];

        foreach (var node in graph.ProjectNodes)
        {
            var projectPath = Path.GetFullPath(node.ProjectInstance.FullPath);
            _allProjects.Add(projectPath);
            _nodeByPath[projectPath] = node;

            // Determine if this is a test project
            if (IsTestProjectNode(node))
                _testProjects.Add(projectPath);

            // Cache source files (.cs) for this project (excluding bin/obj)
            var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
            var sourceFiles = new List<string>();
            if (Directory.Exists(projectDir))
            {
                foreach (var csFile in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
                {
                    if (!IsInBinOrObj(csFile))
                        sourceFiles.Add(Path.GetFullPath(csFile));
                }
            }
            _sourceFilesByProject[projectPath] = sourceFiles;

            // Track directory for ownership lookup
            if (!string.IsNullOrEmpty(projectDir))
                _sortedProjectDirs.Add((projectDir, projectPath));
        }

        // Sort by directory length descending (longest first) so we match most-specific first
        _sortedProjectDirs.Sort((a, b) =>
            b.Directory.Length.CompareTo(a.Directory.Length));
    }

    public string? FindOwningProject(string filePath)
    {
        var normalizedFile = Path.GetFullPath(filePath);

        foreach (var (dir, projPath) in _sortedProjectDirs)
        {
            if (normalizedFile.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalizedFile.StartsWith(dir + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedFile, projPath, StringComparison.OrdinalIgnoreCase))
            {
                return projPath;
            }
        }

        return null;
    }

    public IReadOnlySet<string> GetTransitiveDependents(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        if (!_nodeByPath.TryGetValue(normalized, out var startNode))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectDependents(startNode, result);
        return result;
    }

    public IReadOnlyList<string> GetSourceFiles(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _sourceFilesByProject.TryGetValue(normalized, out var files) ? files : [];
    }

    public bool IsTestProject(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _testProjects.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsTestProjectNode(ProjectGraphNode node)
    {
        // Check <IsTestProject>true</IsTestProject> property
        var isTestProp = node.ProjectInstance.GetPropertyValue("IsTestProject");
        if (string.Equals(isTestProp, "true", StringComparison.OrdinalIgnoreCase))
            return true;

        // Heuristic: references Microsoft.NET.Test.Sdk
        foreach (var item in node.ProjectInstance.GetItems("PackageReference"))
        {
            if (string.Equals(item.EvaluatedInclude, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void CollectDependents(ProjectGraphNode node, HashSet<string> visited)
    {
        foreach (var referencing in node.ReferencingProjects)
        {
            var path = Path.GetFullPath(referencing.ProjectInstance.FullPath);
            if (visited.Add(path))
                CollectDependents(referencing, visited);
        }
    }

    private static bool IsInBinOrObj(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/") || normalized.Contains("/obj/");
    }
}
