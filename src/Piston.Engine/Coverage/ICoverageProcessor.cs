// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Coverage;

/// <summary>
/// Bridges Cobertura XML output from test runs into the coverage store.
/// </summary>
internal interface ICoverageProcessor
{
    /// <summary>
    /// Parses each Cobertura XML file, records the merged line and branch coverage in the store's
    /// coverage report, and attributes all covered lines to every provided test FQN
    /// (run-level granularity).
    /// </summary>
    /// <param name="runId">The run identifier from <see cref="ICoverageStore.CreateRunId"/>.</param>
    /// <param name="coberturaXmlPaths">Paths to the Cobertura XML files produced by coverlet.</param>
    /// <param name="testFqns">Fully-qualified names of tests that ran in this suite.</param>
    /// <param name="store">The coverage store to write into.</param>
    /// <param name="isFullRun">True when every test ran unfiltered, so the report replaces the stored one.</param>
    /// <param name="sourceRoot">
    /// When set, only files under this directory are recorded, so code the tests build and load from elsewhere
    /// (e.g. fixture projects in a temp directory) does not count towards the solution's coverage.
    /// </param>
    /// <param name="impact">
    /// When set and <paramref name="store"/> keeps an impact map, records what each unit executed.
    /// </param>
    Task ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store,
        bool isFullRun = false,
        string? sourceRoot = null,
        CoverageImpactOptions? impact = null);
}

/// <summary>The units a coverage run measured, for the impact map.</summary>
/// <param name="Units">Each unit with the reports that measured it.</param>
/// <param name="Granularity">How the units were measured.</param>
/// <param name="StartedUtc">
/// When the run's build started. Files modified since may differ from what was measured, so they are not
/// recorded.
/// </param>
internal sealed record CoverageImpactOptions(
    IReadOnlyList<CoverageRunUnit> Units,
    Impact.ImpactGranularity Granularity,
    DateTime StartedUtc);

/// <summary>A unit measured by a coverage run.</summary>
/// <param name="ProjectPath">The test project.</param>
/// <param name="UnitKey">The unit within the project: <c>run</c>, a class name or a test name.</param>
/// <param name="ReportPaths">The Cobertura reports produced for it.</param>
/// <param name="TestFqns">The tests that ran.</param>
/// <param name="Merge">True when only some of the unit's tests ran, so the recording adds to what is stored.</param>
internal sealed record CoverageRunUnit(
    string ProjectPath,
    string UnitKey,
    IReadOnlyList<string> ReportPaths,
    IReadOnlyList<string> TestFqns,
    bool Merge);
