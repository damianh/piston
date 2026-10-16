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
    private readonly string _solutionDirectory;

    public MtpTestExecutionStrategy(Func<string, string?> getMtpOutputPath, string solutionDirectory)
    {
        _getMtpOutputPath = getMtpOutputPath;
        _solutionDirectory = solutionDirectory;
    }

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
            _getMtpOutputPath(request.ProjectPath),
            _solutionDirectory,
            request.Filter,
            onProgress,
            ct);

    /// <inheritdoc/>
    public Task<IReadOnlyList<TestResult>> DiscoverAsync(
        string projectPath,
        string? filter,
        Action<IReadOnlyList<TestResult>>? onPartial,
        CancellationToken ct) =>
        MtpTestProcessRunner.DiscoverAsync(projectPath, _getMtpOutputPath(projectPath), _solutionDirectory, filter, onPartial, ct);
}
