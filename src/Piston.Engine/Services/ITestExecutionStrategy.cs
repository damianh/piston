using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// Represents a single test-project execution strategy.
/// Allows plugging in different test execution backends (e.g. process-based, in-process MTP v2).
/// </summary>
public interface ITestExecutionStrategy
{
    /// <summary>
    /// Executes tests for a single project and returns the result.
    /// </summary>
    Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct);

    /// <summary>
    /// Returns true if this strategy is capable of executing the given project.
    /// </summary>
    bool CanExecute(string projectPath);
}
