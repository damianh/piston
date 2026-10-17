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
internal sealed class MtpTestExecutionStrategy : ITestExecutionStrategy
{
    private readonly Func<string, string?> _getMtpOutputPath;
    private readonly Func<string, bool> _isMultiTargeted;
    private readonly string _solutionDirectory;

    public MtpTestExecutionStrategy(
        Func<string, string?> getMtpOutputPath,
        string solutionDirectory,
        Func<string, bool>? isMultiTargeted = null)
    {
        _getMtpOutputPath = getMtpOutputPath;
        _solutionDirectory = solutionDirectory;
        _isMultiTargeted = isMultiTargeted ?? (_ => false);
    }

    /// <summary>
    /// The test app to drive in server mode, or <see langword="null"/> to use <c>dotnet test</c>.
    /// A multi-targeted project has one output per framework but only one is known here, so it always
    /// uses <c>dotnet test --project</c>, which runs every framework.
    /// </summary>
    internal string? ServerAppPath(string projectPath) =>
        _isMultiTargeted(projectPath) ? null : _getMtpOutputPath(projectPath);

    /// <inheritdoc/>
    /// <remarks>Returns <see langword="true"/> only when an MTP output path is available for the project.</remarks>
    public bool CanExecute(string projectPath) =>
        _getMtpOutputPath(projectPath) is not null;

    /// <inheritdoc/>
    public Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) => MtpTestProcessRunner.RunAsync(
            request.ProjectPath,
            ServerAppPath(request.ProjectPath),
            _solutionDirectory,
            request.Filter,
            onProgress,
            ct,
            CoverageFor(request));

    private MtpCoverage CoverageFor(ProjectTestRequest request)
    {
        if (!request.CollectCoverage)
        {
            return MtpCoverage.Off;
        }

        return HasCoverageExtension(_getMtpOutputPath(request.ProjectPath)) ? MtpCoverage.Collect : MtpCoverage.ExtensionMissing;
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
        MtpTestProcessRunner.DiscoverAsync(projectPath, ServerAppPath(projectPath), _solutionDirectory, filter, onPartial, ct);
}

/// <summary>Whether, and why not, an MTP test run collects code coverage.</summary>
internal enum MtpCoverage
{
    Off,
    Collect,

    /// <summary>Coverage was requested but the test app cannot collect it.</summary>
    ExtensionMissing,
}
