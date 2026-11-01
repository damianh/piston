// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Impact;

namespace Piston.Engine.Tests.Impact;

internal sealed class StubSolutionGraph(
    IEnumerable<string> allProjects,
    IEnumerable<string> testProjects,
    Dictionary<string, IReadOnlySet<string>>? transitiveDependents = null,
    Dictionary<string, string>? fileToProject = null,
    Dictionary<string, IReadOnlyList<string>>? projectToFiles = null,
    HashSet<string>? mtpProjects = null,
    Dictionary<string, string>? mtpOutputPaths = null,
    Dictionary<string, IReadOnlySet<string>>? transitiveDependencies = null) : ISolutionGraph
{
    private readonly List<string> _allProjects = allProjects.ToList();
    private readonly List<string> _testProjects = testProjects.ToList();
    private readonly Dictionary<string, IReadOnlySet<string>> _transitiveDependents = transitiveDependents ?? [];
    private readonly Dictionary<string, string> _fileToProject = fileToProject ?? [];
    private readonly Dictionary<string, IReadOnlyList<string>> _projectToFiles = projectToFiles ?? [];
    private readonly HashSet<string> _mtpProjects = mtpProjects ?? [];
    private readonly Dictionary<string, string> _mtpOutputPaths = mtpOutputPaths ?? [];
    private readonly Dictionary<string, IReadOnlySet<string>> _transitiveDependencies = transitiveDependencies ?? [];

    public IReadOnlyList<string> AllProjectPaths => _allProjects;
    public IReadOnlyList<string> TestProjectPaths => _testProjects;
    public bool EvaluatedBeforeRestore { get; set; }

    public string? FindOwningProject(string filePath) =>
        _fileToProject.TryGetValue(filePath, out var proj) ? proj : null;

    public IReadOnlySet<string> GetTransitiveDependents(string projectPath) =>
        _transitiveDependents.TryGetValue(projectPath, out var deps)
            ? deps
            : (IReadOnlySet<string>)new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> GetTransitiveDependencies(string projectPath) =>
        _transitiveDependencies.TryGetValue(projectPath, out var deps)
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
