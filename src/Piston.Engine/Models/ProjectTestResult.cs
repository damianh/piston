// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record ProjectTestResult(
    string ProjectPath,
    IReadOnlyList<TestSuite> Suites,
    string? RunnerError,
    IReadOnlyList<string> CoverageReportPaths,
    bool Crashed);
