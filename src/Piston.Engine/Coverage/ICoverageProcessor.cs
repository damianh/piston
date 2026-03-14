namespace Piston.Engine.Coverage;

/// <summary>
/// Bridges Cobertura XML output from test runs into the coverage store.
/// </summary>
internal interface ICoverageProcessor
{
    /// <summary>
    /// Parses each Cobertura XML file and stores coverage data in the store,
    /// attributing all covered lines to every provided test FQN
    /// (suite-level granularity for Phase 3 v1).
    /// </summary>
    /// <param name="runId">The run identifier from <see cref="ICoverageStore.CreateRunId"/>.</param>
    /// <param name="coberturaXmlPaths">Paths to the Cobertura XML files produced by coverlet.</param>
    /// <param name="testFqns">Fully-qualified names of tests that ran in this suite.</param>
    /// <param name="store">The coverage store to write into.</param>
    Task ProcessCoverageAsync(
        long runId,
        IReadOnlyList<string> coberturaXmlPaths,
        IReadOnlyList<string> testFqns,
        ICoverageStore store);
}
