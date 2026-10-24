// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Graph;
using Piston.Engine.Services;

namespace Piston.Engine.Impact;

internal sealed class MsBuildSolutionGraph : ISolutionGraph
{
    private readonly List<string> _allProjects;
    private readonly List<string> _testProjects;
    // Multi-targeted projects have an outer node plus one inner node per target framework,
    // all sharing a path; each can carry framework-specific references.
    private readonly Dictionary<string, List<ProjectGraphNode>> _nodesByPath;
    private readonly Dictionary<string, IReadOnlyList<string>> _sourceFilesByProject;
    private readonly Dictionary<string, bool> _mtpProjects;
    private readonly Dictionary<string, string> _mtpOutputPaths;
    private readonly HashSet<string> _multiTargetedProjects;
    private readonly Dictionary<string, TestProjectFacts> _testProjectFacts;
    private readonly string _solutionPath;
    private readonly ConcurrentBag<Project> _evaluated = [];

    // Sorted list of (projectDirectory, projectPath) for directory-based ownership lookup
    private readonly List<(string Directory, string ProjectPath)> _sortedProjectDirs;

    public IReadOnlyList<string> AllProjectPaths => _allProjects;
    public IReadOnlyList<string> TestProjectPaths => _testProjects;
    public string? VerificationCoverageIssue => VerificationInputCoverage.FindIssue(_solutionPath, _evaluated);

    public MsBuildSolutionGraph(string solutionPath)
    {
        // MsBuildLocatorGuard.EnsureRegistered() MUST have been called before this
        // constructor is executed (i.e. before this class's assembly is JIT-loaded).
        // The caller (PistonEngine or test setup) is responsible for doing so.

        using var collection = new ProjectCollection();

        _solutionPath = solutionPath;
        var graph = new ProjectGraph([new ProjectGraphEntryPoint(solutionPath)], collection,
            (path, properties, projectCollection) =>
            {
                var project = new Project(path, properties, null, projectCollection);
                _evaluated.Add(project);
                return project.CreateProjectInstance();
            }, CancellationToken.None);

        _allProjects = [];
        _testProjects = [];
        _nodesByPath = new Dictionary<string, List<ProjectGraphNode>>(StringComparer.OrdinalIgnoreCase);
        _sourceFilesByProject = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        _mtpProjects = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        _mtpOutputPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _multiTargetedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _testProjectFacts = new Dictionary<string, TestProjectFacts>(StringComparer.OrdinalIgnoreCase);
        _sortedProjectDirs = [];

        var log = DiagnosticLog.Instance;
        log?.Write("SolutionGraph", $"Loading solution: {solutionPath}");
        log?.Write("SolutionGraph", $"ProjectNodes count: {graph.ProjectNodes.Count}");

        foreach (var node in graph.ProjectNodes)
        {
            var projectPath = Path.GetFullPath(node.ProjectInstance.FullPath);
            _allProjects.Add(projectPath);
            if (!_nodesByPath.TryGetValue(projectPath, out var nodes))
            {
                _nodesByPath[projectPath] = nodes = [];
            }

            nodes.Add(node);

            var assetsFile = node.ProjectInstance.GetPropertyValue("ProjectAssetsFile");
            if (!string.IsNullOrEmpty(assetsFile) && !File.Exists(assetsFile))
            {
                EvaluatedBeforeRestore = true;
            }

            var isTest = IsTestProjectNode(node);
            var isMtp = IsMtpProjectNode(node);

            // Determine if this is a test project.
            // MTP v2 projects are implicitly test projects — they may lack
            // <IsTestProject>true</IsTestProject> or Microsoft.NET.Test.Sdk.
            if (isTest || isMtp)
            {
                _testProjects.Add(projectPath);
                _testProjectFacts[projectPath] = new TestProjectFacts(
                    node.ProjectInstance.GetPropertyValue("OutputType"),
                    isMtp,
                    node.ProjectInstance.GetItems("PackageReference")
                        .Select(item => item.EvaluatedInclude)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase));
            }

            // Determine if this is an MTP v2 project
            if (isMtp)
            {
                _mtpProjects[projectPath] = true;
                var targetPath = node.ProjectInstance.GetPropertyValue("TargetPath");
                if (!string.IsNullOrEmpty(targetPath))
                {
                    _mtpOutputPaths[projectPath] = Path.GetFullPath(targetPath);
                }
            }

            var targetFrameworks = node.ProjectInstance.GetPropertyValue("TargetFrameworks")
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (targetFrameworks > 1)
            {
                _ = _multiTargetedProjects.Add(projectPath);
            }

            log?.Write("SolutionGraph",
                $"  Project: {Path.GetFileName(projectPath)} | " +
                $"isTest={isTest} | isMtp={isMtp}" +
                (isMtp && _mtpOutputPaths.TryGetValue(projectPath, out var mtpOut) ? $" | mtpOutput={mtpOut}" : ""));

            // Cache source files (.cs) for this project (excluding bin/obj)
            var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
            var sourceFiles = new List<string>();
            if (Directory.Exists(projectDir))
            {
                foreach (var csFile in Directory.EnumerateFiles(projectDir, "*.cs", SearchOption.AllDirectories))
                {
                    if (!IsInBinOrObj(csFile))
                    {
                        sourceFiles.Add(Path.GetFullPath(csFile));
                    }
                }
            }
            _sourceFilesByProject[projectPath] = sourceFiles;

            // Track directory for ownership lookup
            if (!string.IsNullOrEmpty(projectDir))
            {
                _sortedProjectDirs.Add((projectDir, projectPath));
            }
        }

        // Sort by directory length descending (longest first) so we match most-specific first
        _sortedProjectDirs.Sort((a, b) =>
            b.Directory.Length.CompareTo(a.Directory.Length));

        log?.Write("SolutionGraph",
            $"Graph loaded: {_allProjects.Count} projects, " +
            $"{_testProjects.Count} test, {_mtpProjects.Count} MTP" +
            (EvaluatedBeforeRestore ? " (some projects not yet restored)" : ""));
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

    public IReadOnlySet<string> GetTransitiveDependents(string projectPath) =>
        Collect(projectPath, static node => node.ReferencingProjects);

    public IReadOnlySet<string> GetTransitiveDependencies(string projectPath) =>
        Collect(projectPath, static node => node.ProjectReferences);

    public IReadOnlyList<string> GetSourceFiles(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _sourceFilesByProject.TryGetValue(normalized, out var files) ? files : [];
    }

    public bool EvaluatedBeforeRestore { get; }

    public bool IsTestProject(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _testProjects.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsMtpProject(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _mtpProjects.ContainsKey(normalized);
    }

    public string? GetMtpOutputPath(string projectPath)
    {
        var normalized = Path.GetFullPath(projectPath);
        return _mtpOutputPaths.TryGetValue(normalized, out var p) ? p : null;
    }

    public bool IsMultiTargeted(string projectPath) =>
        _multiTargetedProjects.Contains(Path.GetFullPath(projectPath));

    public TestProjectFacts? GetTestProjectFacts(string projectPath) =>
        _testProjectFacts.GetValueOrDefault(Path.GetFullPath(projectPath));

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsTestProjectNode(ProjectGraphNode node)
    {
        // Check <IsTestProject>true</IsTestProject> property
        var isTestProp = node.ProjectInstance.GetPropertyValue("IsTestProject");
        if (string.Equals(isTestProp, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Heuristic: references Microsoft.NET.Test.Sdk
        foreach (var item in node.ProjectInstance.GetItems("PackageReference"))
        {
            if (string.Equals(item.EvaluatedInclude, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsMtpProjectNode(ProjectGraphNode node)
    {
        // Check explicit MSBuild properties set by MTP-enabled test frameworks
        var props = new[]
        {
            "IsTestingPlatformApplication",
            "EnableMSTestRunner",
            "UseMicrosoftTestingPlatformRunner",
            "EnableNUnitRunner",
        };

        foreach (var prop in props)
        {
            if (string.Equals(node.ProjectInstance.GetPropertyValue(prop), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Heuristic: direct reference to Microsoft.Testing.Platform package
        foreach (var item in node.ProjectInstance.GetItems("PackageReference"))
        {
            if (string.Equals(item.EvaluatedInclude, "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private HashSet<string> Collect(
        string projectPath, Func<ProjectGraphNode, IEnumerable<ProjectGraphNode>> edges)
    {
        var start = Path.GetFullPath(projectPath);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_nodesByPath.TryGetValue(start, out var startNodes))
        {
            return result;
        }

        // Visit by node identity so every inner build of a multi-targeted project is walked;
        // dedupe by path only in the result.
        var visited = new HashSet<ProjectGraphNode>(startNodes);
        var pending = new Stack<ProjectGraphNode>(startNodes);
        while (pending.TryPop(out var node))
        {
            foreach (var next in edges(node))
            {
                if (!visited.Add(next))
                {
                    continue;
                }

                var path = Path.GetFullPath(next.ProjectInstance.FullPath);
                if (!string.Equals(path, start, StringComparison.OrdinalIgnoreCase))
                {
                    _ = result.Add(path);
                }

                pending.Push(next);
            }
        }

        return result;
    }

    private static bool IsInBinOrObj(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/") || normalized.Contains("/obj/");
    }
}
