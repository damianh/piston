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
    Task ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store,
        bool isFullRun = false);
}
