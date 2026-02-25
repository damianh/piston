using Piston.Core.Models;

namespace Piston.Core.Services;

public interface ITestRunnerService
{
    Task<IReadOnlyList<TestSuite>> RunTestsAsync(string solutionPath, CancellationToken ct);
}
