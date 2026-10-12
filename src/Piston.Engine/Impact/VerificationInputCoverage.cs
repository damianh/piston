// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Microsoft.Build.Evaluation;
using Piston.Engine.Services;

namespace Piston.Engine.Impact;

/// <summary>Fails closed for evaluated user inputs outside the fingerprint's boundaries.</summary>
internal static class VerificationInputCoverage
{
    public static string? FindIssue(string solutionPath, IEnumerable<Project> projects)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var sdkRoot = Path.GetDirectoryName(typeof(Project).Assembly.Location)!;
        var userDotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        var manifestsRoot = Path.GetFullPath(Path.Combine(sdkRoot, "..", "..", "sdk-manifests"));
        foreach (var project in projects)
        {
            var projectDir = Path.GetDirectoryName(project.FullPath)!;
            var packageRoot = project.GetPropertyValue("NuGetPackageRoot");
            var packsRoot = project.GetPropertyValue("NetCoreTargetingPackRoot");
            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var restoreOutputs = new HashSet<string>(pathComparer);
            if (project.GetPropertyValue("MSBuildProjectExtensionsPath") is { Length: > 0 } extensionsPath)
            {
                var extensions = Path.GetFullPath(extensionsPath, projectDir);
                _ = restoreOutputs.Add(Path.Combine(extensions, Path.GetFileName(project.FullPath) + ".nuget.g.props"));
                _ = restoreOutputs.Add(Path.Combine(extensions, Path.GetFileName(project.FullPath) + ".nuget.g.targets"));
            }
            var inputs = new List<string> { project.FullPath };
            inputs.AddRange(project.Imports.Select(i => i.ImportedProject.FullPath));
            foreach (var item in project.Items)
            {
                // FullPath covers evaluated Compile/Content/None/AdditionalFiles/Analyzer and
                // custom file items. Non-file items (package IDs, capabilities) do not exist.
                var path = item.GetMetadataValue("FullPath");
                if (Path.IsPathFullyQualified(path) && IsUnder(item.Xml.ContainingProject.FullPath, sdkRoot)
                    && IsUnder(path, root)
                    && Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
                {
                    // SDK-declared intermediate/output items are products of the covered source.
                    // Explicit user Compile/Import inputs in these directories still fail closed.
                    continue;
                }

                if (File.Exists(path))
                {
                    inputs.Add(path);
                }
                else if (item.ItemType is "Compile" or "ProjectReference")
                {
                    inputs.Add(Path.GetFullPath(item.EvaluatedInclude, projectDir));
                }

                if (item.ItemType == "Reference" && item.GetMetadataValue("HintPath") is { Length: > 0 } hint)
                {
                    inputs.Add(Path.GetFullPath(hint, projectDir));
                }
            }

            foreach (var input in inputs)
            {
                // SDK and restored packages remain the documented toolchain/cache exclusions.
                if (IsUnder(input, sdkRoot)
                    || (Path.IsPathFullyQualified(packsRoot) && IsUnder(input, packsRoot))
                    || IsUnder(input, manifestsRoot)
                    || IsUnder(input, Path.Combine(userDotnetRoot, "packs"))
                    || IsUnder(input, Path.Combine(userDotnetRoot, "sdk-manifests"))
                    || (Path.IsPathFullyQualified(packageRoot) && IsUnder(input, packageRoot))
                    || restoreOutputs.Contains(input))
                {
                    continue;
                }

                if (!SourceFingerprinter.CoversPath(root, input))
                {
                    return $"Build input '{input}' is outside the source fingerprint. " +
                        "Use dotnet test explicitly; verify cannot prove full verification for this solution.";
                }
            }
        }

        return null;
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
