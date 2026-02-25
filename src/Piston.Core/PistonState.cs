using Piston.Core.Models;

namespace Piston.Core;

public sealed class PistonState
{
    public PistonPhase Phase { get; set; }
    public string? SolutionPath { get; set; }
    public BuildResult? LastBuild { get; set; }
    public IReadOnlyList<TestSuite> TestSuites { get; set; } = [];
    public DateTimeOffset? LastRunTime { get; set; }

    public int TotalPassed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Passed);
    public int TotalFailed => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Failed);
    public int TotalSkipped => TestSuites.SelectMany(s => s.Tests).Count(t => t.Status == TestStatus.Skipped);

    public event Action? StateChanged;

    public void NotifyChanged() => StateChanged?.Invoke();
}
