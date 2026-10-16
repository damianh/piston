// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

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

    /// <summary>
    /// Lists the tests in a project without running them (status <see cref="TestStatus.NotRun"/>).
    /// Returns an empty list when the backend does not support discovery.
    /// </summary>
    Task<IReadOnlyList<TestResult>> DiscoverAsync(string projectPath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TestResult>>([]);
}
