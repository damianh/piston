// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// <see cref="ITestExecutionStrategy"/> that executes MTP v2 test projects through the test app's
/// server mode, falling back to <c>dotnet test</c> in Microsoft.Testing.Platform mode.
/// </summary>
/// <remarks>
/// Uses a <see cref="Func{T, TResult}"/> delegate to determine whether a project is
/// an MTP project at execution time, allowing the strategy to be constructed before
/// the MSBuild graph is loaded.
/// <see cref="CanExecute"/> returns <see langword="false"/> when the project has no MTP output
/// path (i.e. it is not an MTP project), causing <see cref="CompositeTestExecutionStrategy"/>
/// to fall back to the VSTest path.
/// </remarks>
internal sealed class MtpTestExecutionStrategy(
    Func<string, string?> getMtpOutputPath,
    string solutionDirectory,
    Func<string, bool>? isMultiTargeted = null,
    MtpWarmHostPool? warmHosts = null,
    Func<string, bool>? requiresResources = null,
    MtpDiscoveryCache? discoveryCache = null,
    Func<string, IReadOnlyList<(string Framework, string OutputPath)>>? getMtpTargetOutputs = null) : ITestExecutionStrategy
{
    private readonly Func<string, bool> _isMultiTargeted = isMultiTargeted ?? (_ => false);
    private readonly Func<string, bool> _requiresResources = requiresResources ?? (_ => false);
    private readonly MtpDiscoveryCache _discoveryCache = discoveryCache ?? new MtpDiscoveryCache();

    /// <summary>
    /// The test app to drive in server mode, or <see langword="null"/> to use <c>dotnet test</c>.
    /// Multi-targeted discovery and runs without verified target outputs use <c>dotnet test --project</c>.
    /// When coverage is requested, verified target outputs run separately even if a collector is missing.
    /// </summary>
    internal string? ServerAppPath(string projectPath) =>
        _isMultiTargeted(projectPath) ? null : getMtpOutputPath(projectPath);

    /// <inheritdoc/>
    /// <remarks>Returns <see langword="true"/> only when an MTP output path is available for the project.</remarks>
    public bool CanExecute(string projectPath) =>
        getMtpOutputPath(projectPath) is not null;

    /// <inheritdoc/>
    public Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        var decision = CoverageFor(request);
        return MtpTestProcessRunner.RunAsync(
            request.ProjectPath,
            ServerAppPath(request.ProjectPath),
            solutionDirectory,
            request.Filter,
            onProgress,
            ct,
            decision.Coverage,
            WarmHostsFor(request.ProjectPath),
            request.ReuseDiscovery ? _discoveryCache : null,
            decision.ExpectedReports,
            decision.TargetOutputs);
    }

    // A successful warm host outlives the resource lease; coordinated projects must exit with it.
    internal MtpWarmHostPool? WarmHostsFor(string projectPath) =>
        _requiresResources(projectPath) ? null : warmHosts;

    internal sealed record CoverageDecision(
        MtpCoverage Coverage, int ExpectedReports, IReadOnlyList<(string Framework, string OutputPath)> TargetOutputs);

    internal CoverageDecision CoverageFor(ProjectTestRequest request)
    {
        if (!request.CollectCoverage)
        {
            return new(MtpCoverage.Off, 0, []);
        }

        if (!_isMultiTargeted(request.ProjectPath))
        {
            var output = getMtpOutputPath(request.ProjectPath);
            if (output is null || !File.Exists(output))
            {
                return new(MtpCoverage.OutputUnverified, 0, []);
            }

            return HasCoverageExtension(output)
                ? new(MtpCoverage.Collect, 1, [])
                : new(MtpCoverage.ExtensionMissing, 0, []);
        }

        var targets = getMtpTargetOutputs?.Invoke(request.ProjectPath) ?? [];
        if (targets.Count < 2 || targets.Any(target => !File.Exists(target.OutputPath) || string.IsNullOrWhiteSpace(target.Framework))
            || targets.Select(target => target.Framework).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count
            || targets.Select(target => target.OutputPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
        {
            return new(MtpCoverage.OutputUnverified, 0, []);
        }

        return targets.All(target => HasCoverageExtension(target.OutputPath))
            ? new(MtpCoverage.Collect, targets.Count, targets)
            : new(MtpCoverage.MultiTargetExtensionMissing, 0, targets);
    }

    /// <summary>
    /// Whether the test app at <paramref name="appPath"/> was built with Microsoft.Testing.Extensions.CodeCoverage.
    /// Its <c>--coverage</c> options are unknown to, and fail, a test app without it.
    /// </summary>
    internal static bool HasCoverageExtension(string? appPath) =>
        Path.GetDirectoryName(appPath) is { Length: > 0 } dir
        && File.Exists(Path.Combine(dir, CoverageExtensionAssembly));

    internal const string CoverageExtensionAssembly = "Microsoft.Testing.Extensions.CodeCoverage.dll";

    /// <inheritdoc/>
    public Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct) =>
        MtpTestProcessRunner.DiscoverAsync(projectPath, ServerAppPath(projectPath), solutionDirectory, filter, onPartial, ct, WarmHostsFor(projectPath));
}
