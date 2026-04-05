using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// <see cref="ITestExecutionStrategy"/> implementation that delegates to
/// <see cref="TestProcessRunner.RunAsync"/> — the universal fallback that spawns a
/// <c>dotnet test</c> process per project.
/// </summary>
internal sealed class ProcessTestExecutionStrategy : ITestExecutionStrategy
{
    private readonly ITestResultParser _parser;

    public ProcessTestExecutionStrategy(ITestResultParser parser)
    {
        _parser = parser;
    }

    /// <inheritdoc/>
    /// <remarks>Always returns <see langword="true"/> — process-based execution works for any project.</remarks>
    public bool CanExecute(string projectPath) => true;

    /// <inheritdoc/>
    public Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct) =>
        TestProcessRunner.RunAsync(
            request.ProjectPath,
            request.Filter,
            request.CollectCoverage,
            onProgress,
            _parser,
            ct);
}
