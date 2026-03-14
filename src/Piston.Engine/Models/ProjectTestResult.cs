namespace Piston.Engine.Models;

public sealed record ProjectTestResult(
    string ProjectPath,
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError,
    IReadOnlyList<string> CoverageReportPaths,
    bool Crashed);
