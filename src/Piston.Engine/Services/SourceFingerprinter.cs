using System.Security.Cryptography;
using System.Text;
using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Computes the deterministic <c>piston-source-v1</c> source-input fingerprint.
/// <para>
/// Inputs: every file under the solution directory, excluding any path with a component named
/// <c>bin</c>, <c>obj</c>, <c>TestResults</c>, <c>.git</c>, <c>.vs</c>, <c>.idea</c>, <c>.piston</c>
/// or <c>node_modules</c>; plus, for each of <c>global.json</c>, <c>Directory.Build.props</c>,
/// <c>Directory.Build.targets</c>, <c>Directory.Build.rsp</c>, <c>Directory.Packages.props</c>,
/// <c>NuGet.config</c> and <c>nuget.config</c> not present in the solution directory itself, the
/// nearest ancestor occurrence.
/// </para>
/// <para>
/// Digest: SHA-256 over the UTF-8 tag <c>piston-source-v1</c> followed by a NUL byte, then for each
/// input (ordered ordinally by its '/'-separated relative path with '/' mapped to NUL) the UTF-8
/// relative path, NUL, file bytes, NUL. Ancestor paths are relative (e.g. <c>../global.json</c>).
/// </para>
/// Project sources outside the solution directory, SDK/workload installs and the NuGet package cache
/// are not included; the run context records the SDK separately.
/// </summary>
public static class SourceFingerprinter
{
    public const string Algorithm = "piston-source-v1";

    private static readonly HashSet<string> ExcludedComponents = new(StringComparer.Ordinal)
    {
        "bin", "obj", "TestResults", ".git", ".vs", ".idea", ".piston", "node_modules",
    };

    private static readonly string[] AncestorInputs =
    [
        "global.json", "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp",
        "Directory.Packages.props", "NuGet.config", "nuget.config",
    ];

    public static SourceFingerprint Compute(string rootDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var inputs = new List<(string Relative, string Path)>();

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').Any(ExcludedComponents.Contains))
                continue;
            inputs.Add((relative, path));
        }

        foreach (var name in AncestorInputs)
        {
            if (File.Exists(Path.Combine(root, name)))
                continue;
            var depth = 0;
            for (var dir = Directory.GetParent(root); dir is not null; dir = dir.Parent)
            {
                depth++;
                var candidate = Path.Combine(dir.FullName, name);
                if (!File.Exists(candidate)) continue;
                inputs.Add((string.Concat(Enumerable.Repeat("../", depth)) + name, candidate));
                break;
            }
        }

        inputs.Sort((a, b) => string.CompareOrdinal(a.Relative.Replace('/', '\0'), b.Relative.Replace('/', '\0')));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(Algorithm));
        hash.AppendData([0]);
        foreach (var (relative, path) in inputs)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(relative));
            hash.AppendData([0]);
            hash.AppendData(File.ReadAllBytes(path));
            hash.AppendData([0]);
        }

        return new SourceFingerprint(Algorithm, Convert.ToHexStringLower(hash.GetHashAndReset()), inputs.Count);
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
