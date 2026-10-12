// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Build.Evaluation;
using Piston.Engine.Services;

namespace Piston.Engine.Impact;

/// <summary>Fails closed for evaluated user inputs outside the fingerprint's boundaries.</summary>
internal static class VerificationInputCoverage
{
    // Item types whose include is a file path consumed by build, compile or copy-to-output.
    private static readonly HashSet<string> DeclaredFileItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content",
        "None",
        "EmbeddedResource",
        "AdditionalFiles",
        "Analyzer",
        "EditorConfigFiles",
        "GlobalAnalyzerConfigFiles",
        "Resource",
        "Page",
        "ApplicationDefinition",
    };

    public static string? FindIssue(string solutionPath, IEnumerable<Project> projects)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var sdkRoot = Path.GetDirectoryName(typeof(Project).Assembly.Location)!;
        var userDotnetRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        var manifestsRoot = Path.GetFullPath(Path.Combine(sdkRoot, "..", "..", "sdk-manifests"));
        var installedPacksRoot = Path.GetFullPath(Path.Combine(sdkRoot, "..", "..", "packs"));
        var evaluatedProjects = projects.ToList();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var generatedRuntimeConfigs = evaluatedProjects
            .Where(project => SourceFingerprinter.CoversPath(root, project.FullPath)
                && string.Equals(project.GetPropertyValue("GenerateRuntimeConfigurationFiles"), "true",
                    StringComparison.OrdinalIgnoreCase))
            .Select(project => project.GetPropertyValue("ProjectRuntimeConfigFilePath"))
            .Where(Path.IsPathFullyQualified)
            .Select(Path.GetFullPath)
            .ToHashSet(pathComparer);
        foreach (var project in evaluatedProjects)
        {
            var projectDir = Path.GetDirectoryName(project.FullPath)!;
            var packageRoot = project.GetPropertyValue("NuGetPackageRoot");
            var packsRoot = project.GetPropertyValue("NetCoreTargetingPackRoot");
            var configuredExclusions = new[] { packageRoot, packsRoot }
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => Path.GetFullPath(path, projectDir))
                .ToArray();
            var restoreOutputs = new HashSet<string>(pathComparer);
            string? extensions = null;
            if (project.GetPropertyValue("MSBuildProjectExtensionsPath") is { Length: > 0 } extensionsPath)
            {
                extensions = Path.GetFullPath(extensionsPath, projectDir);
                _ = restoreOutputs.Add(Path.Combine(extensions, Path.GetFileName(project.FullPath) + ".nuget.g.props"));
                _ = restoreOutputs.Add(Path.Combine(extensions, Path.GetFileName(project.FullPath) + ".nuget.g.targets"));
            }
            var packageDirectories = extensions is null
                ? new HashSet<string>(pathComparer)
                : GetRestoredPackageDirectories(project, projectDir, extensions, pathComparer);
            var packageRootPath = string.IsNullOrWhiteSpace(packageRoot)
                ? null
                : Path.GetFullPath(packageRoot, projectDir);
            var protectedInputs = new List<string> { project.FullPath };
            var inputs = new List<string> { project.FullPath };
            foreach (var import in project.Imports)
            {
                var importedPath = import.ImportedProject.FullPath;
                inputs.Add(importedPath);
                var isRestoredPackageImport = packageRootPath is not null
                    && packageDirectories.Any(packageDirectory => IsUnder(importedPath, packageDirectory))
                    && IsUnder(importedPath, packageRootPath)
                    && (restoreOutputs.Contains(import.ImportingElement.ContainingProject.FullPath)
                        || IsUnder(import.ImportingElement.ContainingProject.FullPath, sdkRoot)
                        || IsUnder(import.ImportingElement.ContainingProject.FullPath, manifestsRoot)
                        || packageDirectories.Any(packageDirectory =>
                            IsUnder(import.ImportingElement.ContainingProject.FullPath, packageDirectory)));
                var isInstalledPackImport = IsInstalledPackInput(importedPath,
                    import.ImportingElement.ContainingProject.FullPath, sdkRoot, userDotnetRoot);
                if (!isRestoredPackageImport && !isInstalledPackImport
                    && !IsUnder(importedPath, sdkRoot) && !IsUnder(importedPath, manifestsRoot))
                {
                    protectedInputs.Add(importedPath);
                }
            }

            foreach (var item in project.Items)
            {
                // FullPath covers evaluated Compile/Content/None/AdditionalFiles/Analyzer and
                // custom file items. Non-file items (package IDs, capabilities) do not exist.
                var path = item.GetMetadataValue("FullPath");
                var itemOrigin = item.Xml.ContainingProject.FullPath;
                var isRestoredPackageOrigin = restoreOutputs.Contains(itemOrigin)
                    || IsUnder(itemOrigin, sdkRoot)
                    || IsUnder(itemOrigin, manifestsRoot)
                    || packageDirectories.Any(packageDirectory => IsUnder(itemOrigin, packageDirectory));
                if (item.ItemType is "PotentialEditorConfigFiles" or "EditorConfigFiles"
                    && IsUnder(itemOrigin, sdkRoot))
                {
                    continue;
                }

                if (Path.IsPathFullyQualified(path) && IsUnder(itemOrigin, sdkRoot)
                    && IsUnder(path, root)
                    && Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
                {
                    // SDK-declared intermediate/output items are products of the covered source.
                    // Explicit user Compile/Import inputs in these directories still fail closed.
                    continue;
                }

                var pathExists = File.Exists(path);
                // A user-declared file item still feeds the build when its file is missing (the build
                // fails), so it must be judged by declaration rather than by existence.
                var isDeclaredFileItem = item.ItemType is "Compile" or "ProjectReference"
                    || (DeclaredFileItemTypes.Contains(item.ItemType)
                        && !isRestoredPackageOrigin
                        && !IsUnder(itemOrigin, installedPacksRoot)
                        && !IsUnder(itemOrigin, Path.Combine(userDotnetRoot, "packs")));
                var isFileInput = pathExists || isDeclaredFileItem;
                var isInstalledPackInput = Path.IsPathFullyQualified(path)
                    && IsInstalledPackInput(path, itemOrigin, sdkRoot, userDotnetRoot);
                var isRestoredPackageItem = packageRootPath is not null
                    && Path.IsPathFullyQualified(path)
                    && IsUnder(path, packageRootPath)
                    && packageDirectories.Any(packageDirectory => IsUnder(path, packageDirectory))
                    && isRestoredPackageOrigin;
                // A referenced project's runtime config is an SDK-produced output, not arbitrary data under bin/obj.
                var isGeneratedOutput = Path.IsPathFullyQualified(path)
                    && generatedRuntimeConfigs.Contains(Path.GetFullPath(path))
                    && item.ItemType is "Content" or "None";
                if (pathExists && !isInstalledPackInput && !isRestoredPackageItem && !isGeneratedOutput)
                {
                    inputs.Add(path);
                }
                else if (!pathExists && isDeclaredFileItem
                    && !isInstalledPackInput && !isRestoredPackageItem && !isGeneratedOutput)
                {
                    inputs.Add(Path.GetFullPath(item.EvaluatedInclude, projectDir));
                }

                if (item.ItemType is "MSBuildSourceProjectFile" or "ContentWithTargetPath"
                    && IsUnder(itemOrigin, sdkRoot)
                    && Path.IsPathFullyQualified(path)
                    && IsUnder(path, root)
                    && Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                        .Any(component => component is "bin" or "obj"))
                {
                    continue;
                }

                if (item.ItemType == "Compile" && Path.IsPathFullyQualified(path))
                {
                    for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!);
                        directory is not null;
                        directory = directory.Parent)
                    {
                        var editorConfig = Path.Combine(directory.FullName, ".editorconfig");
                        if (File.Exists(editorConfig))
                        {
                            inputs.Add(editorConfig);
                            if (IsEditorConfigRoot(editorConfig))
                            {
                                break;
                            }
                        }
                    }
                }

                if (!isInstalledPackInput && !isRestoredPackageItem && !isGeneratedOutput
                    && isFileInput && Path.IsPathFullyQualified(path))
                {
                    protectedInputs.Add(path);
                }

                if (item.ItemType == "Reference" && item.GetMetadataValue("HintPath") is { Length: > 0 } hint)
                {
                    var hintPath = Path.GetFullPath(hint, projectDir);
                    inputs.Add(hintPath);
                    var isRestoredPackageHint = packageRootPath is not null
                        && IsUnder(hintPath, packageRootPath)
                        && packageDirectories.Any(packageDirectory => IsUnder(hintPath, packageDirectory))
                        && isRestoredPackageOrigin;
                    if (!isRestoredPackageHint)
                    {
                        protectedInputs.Add(hintPath);
                    }
                }
            }

            foreach (var xml in new[] { project.Xml }.Concat(project.Imports.Select(i => i.ImportedProject)))
            {
                foreach (var usingTask in xml.UsingTasks)
                {
                    if (string.IsNullOrWhiteSpace(usingTask.AssemblyFile))
                    {
                        continue;
                    }

                    var sourceDirectory = Path.GetDirectoryName(xml.FullPath)!;
                    var assemblyFile = usingTask.AssemblyFile
                        .Replace("$(MSBuildThisFileFullPath)", xml.FullPath, StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileDirectory)", sourceDirectory + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileName)", Path.GetFileNameWithoutExtension(xml.FullPath),
                            StringComparison.OrdinalIgnoreCase)
                        .Replace("$(MSBuildThisFileExtension)", Path.GetExtension(xml.FullPath),
                            StringComparison.OrdinalIgnoreCase);
                    var expandedAssemblyFile = project.ExpandString(assemblyFile);
                    if (expandedAssemblyFile.Contains("$(", StringComparison.Ordinal)
                        || expandedAssemblyFile.Contains("@(", StringComparison.Ordinal))
                    {
                        return $"Cannot establish coverage for UsingTask AssemblyFile '{usingTask.AssemblyFile}' " +
                            $"in '{xml.FullPath}'. Use dotnet test explicitly; verify cannot prove full verification for this solution.";
                    }

                    var assemblyPath = Path.GetFullPath(expandedAssemblyFile, sourceDirectory);
                    inputs.Add(assemblyPath);
                    var isRestoredPackageTask = packageRootPath is not null
                        && IsUnder(assemblyPath, packageRootPath)
                        && packageDirectories.Any(packageDirectory => IsUnder(assemblyPath, packageDirectory))
                        && (IsUnder(xml.FullPath, sdkRoot)
                            || IsUnder(xml.FullPath, manifestsRoot)
                            || packageDirectories.Any(packageDirectory => IsUnder(xml.FullPath, packageDirectory)));
                    if (!isRestoredPackageTask
                        && !IsInstalledPackInput(assemblyPath, xml.FullPath, sdkRoot, userDotnetRoot))
                    {
                        protectedInputs.Add(assemblyPath);
                    }
                }
            }

            foreach (var exclusionRoot in configuredExclusions)
            {
                var overlappingInput = protectedInputs.FirstOrDefault(input => IsUnder(input, exclusionRoot));
                if (overlappingInput is not null)
                {
                    return $"Build input '{overlappingInput}' under '{exclusionRoot}' is also a project, import, or source input; " +
                        "verification cannot safely apply the package/targeting-pack exclusion. " +
                        "Use dotnet test explicitly; verify cannot prove full verification for this solution.";
                }
            }

            foreach (var input in inputs)
            {
                // SDK and restored packages remain the documented toolchain/cache exclusions.
                if (IsUnder(input, sdkRoot)
                    || configuredExclusions.Any(exclusionRoot => IsUnder(input, exclusionRoot))
                    || IsUnder(input, installedPacksRoot)
                    || IsUnder(input, manifestsRoot)
                    || IsUnder(input, Path.Combine(userDotnetRoot, "packs"))
                    || IsUnder(input, Path.Combine(userDotnetRoot, "sdk-manifests"))
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

    internal static bool IsInstalledPackInput(string inputPath, string declaringPath, string sdkRoot, string userDotnetRoot)
    {
        var dotnetRoot = Path.GetFullPath(Path.Combine(sdkRoot, "..", ".."));
        var installedPacks = Path.Combine(dotnetRoot, "packs");
        var userPacks = Path.Combine(userDotnetRoot, "packs");
        return (IsUnder(inputPath, installedPacks) || IsUnder(inputPath, userPacks))
            && (IsUnder(declaringPath, sdkRoot)
                || IsUnder(declaringPath, Path.Combine(dotnetRoot, "sdk-manifests"))
                || IsUnder(declaringPath, installedPacks)
                || IsUnder(declaringPath, userPacks));
    }

    private static bool IsEditorConfigRoot(string path)
    {
        try
        {
            return File.ReadLines(path).Any(line =>
            {
                var setting = line.Split(['#', ';'], 2)[0].Split('=', 2);
                return setting.Length == 2
                    && string.Equals(setting[0].Trim(), "root", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(setting[1].Trim(), "true", StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static HashSet<string> GetRestoredPackageDirectories(
        Project project,
        string projectDirectory,
        string extensionsDirectory,
        StringComparer pathComparer)
    {
        var directories = new HashSet<string>(pathComparer);
        var assetsFile = project.GetPropertyValue("ProjectAssetsFile");
        if (string.IsNullOrWhiteSpace(assetsFile))
        {
            return directories;
        }

        var fullAssetsFile = Path.GetFullPath(assetsFile, projectDirectory);
        if (!IsUnder(fullAssetsFile, extensionsDirectory) || !File.Exists(fullAssetsFile))
        {
            return directories;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullAssetsFile));
            var root = document.RootElement;
            if (!root.TryGetProperty("packageFolders", out var packageFolders)
                || !root.TryGetProperty("libraries", out var libraries))
            {
                return directories;
            }

            foreach (var packageFolder in packageFolders.EnumerateObject())
            {
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type)
                        || !string.Equals(type.GetString(), "package", StringComparison.OrdinalIgnoreCase)
                        || !library.Value.TryGetProperty("path", out var packagePath))
                    {
                        continue;
                    }

                    var directory = Path.GetFullPath(packagePath.GetString()!, packageFolder.Name);
                    if (IsUnder(directory, packageFolder.Name))
                    {
                        _ = directories.Add(directory);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            directories.Clear();
        }

        return directories;
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
