// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Computes the deterministic <c>piston-source-v2</c> source-input fingerprint, and the
/// <c>piston-source-v3</c> fingerprint over a project-scoped <see cref="SourceInputScope"/>.
/// <para>
/// Inputs: every file under the solution directory, excluding any path with a component named
/// <c>bin</c>, <c>obj</c>, <c>TestResults</c>, <c>.git</c>, <c>.vs</c>, <c>.idea</c>, <c>.piston</c>
/// or <c>node_modules</c>; plus, for each of <c>global.json</c>, <c>Directory.Build.props</c>,
/// <c>Directory.Build.targets</c>, <c>Directory.Build.rsp</c>, <c>Directory.Packages.props</c>,
/// <c>NuGet.config</c> and <c>nuget.config</c> not present in the solution directory itself, the
/// nearest ancestor occurrence.
/// </para>
/// <para>
/// Digest: SHA-256 over the UTF-8 tag <c>piston-source-v2</c> followed by a NUL byte, then for each
/// input (ordered ordinally by its '/'-separated relative path with '/' mapped to NUL) the UTF-8
/// relative path and file bytes, each prefixed by its unsigned 64-bit big-endian byte length.
/// Ancestor paths are relative (e.g. <c>../global.json</c>).
/// </para>
/// Project sources outside the solution directory, SDK/workload installs and the NuGet package cache
/// are not included; the run context records the SDK separately.
/// </summary>
public static class SourceFingerprinter
{
    public const string Algorithm = "piston-source-v2";

    private static readonly HashSet<string> ExcludedComponents = new(StringComparer.Ordinal)
    {
        "bin", "obj", "TestResults", ".git", ".vs", ".idea", ".piston", "node_modules",
    };

    private static readonly string[] AncestorInputs =
    [
        "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
        "Directory.Packages.props", "NuGet.config", "nuget.config",
    ];

    internal static IReadOnlyList<string> AncestorInputNames => AncestorInputs;

    internal static bool CoversPath(string rootDirectory, string path)
    {
        var root = Path.GetFullPath(rootDirectory);
        var fullPath = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, fullPath);
        if (!Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return !relative.Split(Path.DirectorySeparatorChar).Any(ExcludedComponents.Contains);
        }

        foreach (var name in AncestorInputs)
        {
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                if (string.Equals(candidate, fullPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    return true;
                }

                break;
            }
        }

        return false;
    }

    public static SourceFingerprint Compute(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var inputs = new List<(string Relative, string Path)>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(ExcludedComponents.Contains))
            {
                continue;
            }

            inputs.Add((relative, path));
        }

        AddAncestorInputs(root, inputs);
        return Digest(Algorithm, inputs);
    }

    /// <summary>
    /// Fingerprints the inputs of <paramref name="scope"/>. A directory scope is <see cref="Compute(string)"/>;
    /// a project scope (<c>piston-source-v3</c>) hashes, in the same format, the solution file, the files in
    /// project directories git does not ignore (all of them when git is unavailable), the build-configuration
    /// files on the way to them, the declared inputs that exist, and the nearest ancestor inputs.
    /// </summary>
    public static SourceFingerprint Compute(SourceInputScope scope)
    {
        if (!scope.IsProjectScoped)
        {
            return Compute(scope.Root);
        }

        var root = scope.Root;
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var byPath = new Dictionary<string, string>(comparer);

        void Add(string fullPath)
        {
            if (byPath.ContainsKey(fullPath) || !CoversPath(root, fullPath) || !File.Exists(fullPath))
            {
                return;
            }

            byPath.Add(fullPath, Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/'));
        }

        Add(scope.SolutionPath!);
        foreach (var directory in scope.AnchorDirectories)
        {
            foreach (var name in SourceInputScope.AnchorFileNames)
            {
                Add(Path.Combine(directory, name));
            }
        }

        foreach (var input in scope.DeclaredInputs)
        {
            Add(input);
        }

        var cones = scope.ProjectDirectories;
        var relativeCones = cones
            .Select(cone => Path.GetRelativePath(root, cone).Replace(Path.DirectorySeparatorChar, '/'))
            .ToArray();
        if (cones.Count > 0 && GitWorkTree.ListFiles(root, relativeCones) is { } listed)
        {
            foreach (var entry in listed)
            {
                var fullPath = Path.GetFullPath(Path.Combine(root, entry.TrimEnd('/')));
                if (!scope.IsInProjectDirectory(fullPath))
                {
                    continue;
                }

                if (Directory.Exists(fullPath))
                {
                    // A submodule or nested repository: git does not list its files.
                    foreach (var file in EnumerateFiles(fullPath))
                    {
                        Add(file);
                    }
                }
                else
                {
                    Add(fullPath);
                }
            }
        }
        else
        {
            foreach (var cone in cones)
            {
                foreach (var file in EnumerateFiles(cone))
                {
                    Add(file);
                }
            }
        }

        var inputs = byPath.Select(pair => (Relative: pair.Value, Path: pair.Key)).ToList();
        AddAncestorInputs(root, inputs);
        return Digest(SourceInputScope.ProjectsAlgorithm, inputs);
    }

    /// <summary>Returns null when the inputs cannot be read consistently (e.g. a file vanished mid-scan).</summary>
    public static SourceFingerprint? TryCompute(SourceInputScope scope)
    {
        try
        {
            return Compute(scope);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.TryPop(out var current))
        {
            foreach (var file in Directory.EnumerateFiles(current))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!ExcludedComponents.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static void AddAncestorInputs(string root, List<(string Relative, string Path)> inputs)
    {
        foreach (var name in AncestorInputs)
        {
            if (File.Exists(Path.Combine(root, name)))
            {
                continue;
            }

            var depth = 0;
            for (var dir = Directory.GetParent(root); dir is not null; dir = dir.Parent)
            {
                depth++;
                var candidate = Path.Combine(dir.FullName, name);
                if (!File.Exists(candidate))
                {
                    continue;
                }

                inputs.Add((string.Concat(Enumerable.Repeat("../", depth)) + name, candidate));
                break;
            }
        }
    }

    private static SourceFingerprint Digest(string algorithm, List<(string Relative, string Path)> inputs)
    {
        inputs.Sort((a, b) => string.CompareOrdinal(a.Relative.Replace('/', '\0'), b.Relative.Replace('/', '\0')));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(algorithm));
        hash.AppendData([0]);
        Span<byte> length = stackalloc byte[sizeof(ulong)];
        foreach (var (relative, path) in inputs)
        {
            var name = Encoding.UTF8.GetBytes(relative);
            var content = File.ReadAllBytes(path);
            BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)name.Length);
            hash.AppendData(length);
            hash.AppendData(name);
            BinaryPrimitives.WriteUInt64BigEndian(length, (ulong)content.Length);
            hash.AppendData(length);
            hash.AppendData(content);
        }

        return new SourceFingerprint(algorithm, Convert.ToHexStringLower(hash.GetHashAndReset()), inputs.Count);
    }

    /// <summary>Returns null when the inputs cannot be read consistently (e.g. a file vanished mid-scan).</summary>
    public static SourceFingerprint? TryCompute(string rootDirectory)
    {
        try
        {
            return Compute(rootDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
