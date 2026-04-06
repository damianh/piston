using Piston.Engine.Models;

namespace Piston.Engine.Services;

/// <summary>
/// An <see cref="ITestExecutionStrategy"/> that routes to the MTP strategy when
/// <see cref="MtpTestExecutionStrategy.CanExecute"/> returns <see langword="true"/>,
/// falling back to the VSTest strategy otherwise.
/// </summary>
/// <remarks>
/// This is the primary strategy installed in <see cref="Piston.Engine.PistonEngine"/>.
/// It keeps the VSTest code path completely unchanged — MTP support is purely additive.
/// </remarks>
internal sealed class CompositeTestExecutionStrategy : ITestExecutionStrategy
{
    private readonly MtpTestExecutionStrategy _mtpStrategy;
    private readonly ProcessTestExecutionStrategy _vsTestStrategy;

    public CompositeTestExecutionStrategy(
        MtpTestExecutionStrategy mtpStrategy,
        ProcessTestExecutionStrategy vsTestStrategy)
    {
        _mtpStrategy    = mtpStrategy;
        _vsTestStrategy = vsTestStrategy;
    }

    /// <inheritdoc/>
    /// <remarks>Always returns <see langword="true"/> — falls back to VSTest when MTP is unavailable.</remarks>
    public bool CanExecute(string projectPath) => true;

    /// <inheritdoc/>
    public Task<ProjectTestResult> ExecuteAsync(
        ProjectTestRequest request,
        Action<IReadOnlyList<TestSuite>>? onProgress,
        CancellationToken ct)
    {
        var useMtp = _mtpStrategy.CanExecute(request.ProjectPath);
        DiagnosticLog.Instance?.Write("CompositeStrategy",
            $"Routing '{Path.GetFileName(request.ProjectPath)}': " +
            $"useMtp={useMtp} | filter={request.Filter ?? "(none)"}");

        return useMtp
            ? _mtpStrategy.ExecuteAsync(request, onProgress, ct)
            : _vsTestStrategy.ExecuteAsync(request, onProgress, ct);
    }
}
