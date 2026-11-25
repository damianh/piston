// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Services;

/// <summary>
/// The files that count as source inputs: what the watcher reacts to and what the source fingerprint hashes.
/// <para>
/// <see cref="ForDirectory"/> covers every file under the solution directory (<c>piston-source-v2</c>); it
/// applies until the solution graph has been evaluated. <see cref="ForProjects"/> (<c>piston-source-v3</c>)
/// covers the solution file, the directory of every project under the solution directory (git-ignored files
/// there excluded), build-configuration files such as <c>Directory.Build.props</c> in the solution directory
/// and the directories between it and each project, and every evaluated build input under the solution
/// directory, ignored or not. Files tests write elsewhere, e.g. a cache under the repository root, are not
/// inputs. Both exclude <c>bin</c>, <c>obj</c> and the other generated or tool directories, and include the
/// nearest ancestor <c>global.json</c>, <c>Directory.Build.*</c>, <c>Directory.Packages.props</c> and
/// <c>NuGet.config</c>.
/// </para>
/// </summary>
public sealed class SourceInputScope
{
    public const string ProjectsAlgorithm = "piston-source-v3";

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static readonly string[] AnchorNames =
    [
        .. SourceFingerprinter.AncestorInputNames,
        ".editorconfig", "Directory.Solution.props", "Directory.Solution.targets",
        // Ignore rules select which project-directory files are inputs.
        ".gitignore",
    ];

    private readonly string[] _cones;
    private readonly HashSet<string> _declaredInputs;
    private readonly HashSet<string> _anchorDirectories;

    private SourceInputScope(string root, string? solutionPath, string[] cones, HashSet<string> declaredInputs,
        HashSet<string> anchorDirectories)
    {
        Root = root;
        SolutionPath = solutionPath;
        _cones = cones;
        _declaredInputs = declaredInputs;
        _anchorDirectories = anchorDirectories;
    }

    /// <summary>The solution directory.</summary>
    public string Root { get; }

    /// <summary>The solution file of a project scope, or null for a directory scope.</summary>
    public string? SolutionPath { get; }

    /// <summary>Whether inputs are limited to project directories and declared build inputs.</summary>
    public bool IsProjectScoped => SolutionPath is not null;

    /// <summary>The algorithm identifier of fingerprints computed over this scope.</summary>
    public string Algorithm => IsProjectScoped ? ProjectsAlgorithm : SourceFingerprinter.Algorithm;

    /// <summary>Project directories under <see cref="Root"/>, excluding ones nested in another.</summary>
    internal IReadOnlyList<string> ProjectDirectories => _cones;

    /// <summary>Evaluated build inputs under <see cref="Root"/>, present or not.</summary>
    internal IReadOnlyCollection<string> DeclaredInputs => _declaredInputs;

    /// <summary>
    /// <see cref="Root"/> and the directories between it and each project directory, where build-configuration
    /// files apply to projects.
    /// </summary>
    internal IReadOnlyCollection<string> AnchorDirectories => _anchorDirectories;

    internal static IReadOnlyList<string> AnchorFileNames => AnchorNames;

    /// <summary>Every file under <paramref name="rootDirectory"/> is an input.</summary>
    public static SourceInputScope ForDirectory(string rootDirectory) =>
        new(Path.GetFullPath(rootDirectory), null, [], new HashSet<string>(PathComparer), new HashSet<string>(PathComparer));

    /// <summary>
    /// Inputs are the solution file, project directories, build-configuration files on the way to them and
    /// <paramref name="declaredInputs"/>; paths outside the solution directory's fingerprint are ignored.
    /// </summary>
    public static SourceInputScope ForProjects(
        string solutionPath, IEnumerable<string> projectPaths, IEnumerable<string> declaredInputs)
    {
        var solution = Path.GetFullPath(solutionPath);
        var root = Path.GetDirectoryName(solution)!;
        var directories = projectPaths
            .Select(path => Path.GetDirectoryName(Path.GetFullPath(path)))
            .OfType<string>()
            .Where(directory => IsUnderOrEqual(directory, root) && SourceFingerprinter.CoversPath(root, directory))
            .Distinct(PathComparer)
            .OrderBy(directory => directory.Length)
            .ToList();
        var cones = new List<string>();
        foreach (var directory in directories)
        {
            if (!cones.Any(cone => IsUnderOrEqual(directory, cone)))
            {
                cones.Add(directory);
            }
        }

        var declared = declaredInputs
            .Select(path => Path.GetFullPath(path))
            .Where(path => IsUnderOrEqual(path, root) && SourceFingerprinter.CoversPath(root, path))
            .ToHashSet(PathComparer);

        var anchors = new HashSet<string>(PathComparer) { root };
        foreach (var cone in cones)
        {
            AddAncestorsWithin(anchors, Path.GetDirectoryName(cone), root);
        }

        foreach (var input in declared)
        {
            if (IsUnderOrEqual(input, root) && !cones.Any(cone => IsUnderOrEqual(input, cone)))
            {
                AddAncestorsWithin(anchors, Path.GetDirectoryName(input), root);
            }
        }

        return new SourceInputScope(root, solution, [.. cones], declared, anchors);
    }

    /// <summary>Whether a change to <paramref name="path"/>, present or not, may change the fingerprint.</summary>
    public bool Covers(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsProjectScoped || !IsUnderOrEqual(fullPath, Root))
        {
            return SourceFingerprinter.CoversPath(Root, fullPath);
        }

        if (!SourceFingerprinter.CoversPath(Root, fullPath))
        {
            return false;
        }

        return string.Equals(fullPath, SolutionPath, PathComparison)
            || _declaredInputs.Contains(fullPath)
            || IsInProjectDirectory(fullPath)
            || IsAnchorFile(fullPath)
            // Deleting or moving a directory on the way to inputs removes them without per-file events.
            || _anchorDirectories.Contains(fullPath);
    }

    /// <summary>
    /// Whether the watcher must observe <paramref name="directory"/>: it lies in a project directory or on the
    /// way from the solution directory to one or to a declared input.
    /// </summary>
    public bool MayContainInputs(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        if (!IsUnderOrEqual(fullPath, Root) || !SourceFingerprinter.CoversPath(Root, fullPath))
        {
            return false;
        }

        return !IsProjectScoped || _anchorDirectories.Contains(fullPath) || IsInProjectDirectory(fullPath);
    }

    /// <summary>
    /// Whether <paramref name="path"/> is an input only because it lies in a project directory, so a
    /// git-ignored file there is not an input.
    /// </summary>
    internal bool IsProjectDirectoryOnlyInput(string path)
    {
        if (!IsProjectScoped)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        return IsInProjectDirectory(fullPath)
            && !string.Equals(fullPath, SolutionPath, PathComparison)
            && !_declaredInputs.Contains(fullPath)
            && !IsAnchorFile(fullPath)
            && SourceFingerprinter.CoversPath(Root, fullPath);
    }

    /// <summary>Whether both scopes select the same inputs.</summary>
    public bool HasSameInputs(SourceInputScope? other) =>
        other is not null
        && string.Equals(Root, other.Root, PathComparison)
        && string.Equals(SolutionPath, other.SolutionPath, PathComparison)
        && _cones.Length == other._cones.Length
        && _cones.ToHashSet(PathComparer).SetEquals(other._cones)
        && _declaredInputs.SetEquals(other._declaredInputs);

    internal bool IsInProjectDirectory(string fullPath) => _cones.Any(cone => IsUnderOrEqual(fullPath, cone));

    private bool IsAnchorFile(string fullPath) =>
        AnchorNames.Any(name => string.Equals(name, Path.GetFileName(fullPath), PathComparison))
        && Path.GetDirectoryName(fullPath) is { } directory
        && _anchorDirectories.Contains(directory);

    private static void AddAncestorsWithin(HashSet<string> directories, string? directory, string root)
    {
        for (var current = directory;
             current is not null && IsUnderOrEqual(current, root);
             current = Path.GetDirectoryName(current))
        {
            if (!directories.Add(current))
            {
                return;
            }
        }
    }

    internal static bool IsUnderOrEqual(string path, string directory)
    {
        if (string.Equals(path, directory, PathComparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, PathComparison);
    }
}
