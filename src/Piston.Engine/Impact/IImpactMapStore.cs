// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>Persisted map of which tests executed which code, used to select the tests a change affects.</summary>
internal interface IImpactMapStore
{
    /// <summary>Records what a measured unit executed.</summary>
    Task RecordImpactAsync(ImpactRecording recording);

    /// <summary>
    /// Selects the tests whose executed code <paramref name="changes"/> touch. A test measured at a finer
    /// granularity is judged by that evidence alone while it is current, so coarser shared recordings never
    /// widen its selection.
    /// </summary>
    ImpactSelection SelectTests(IReadOnlyList<ChangedFile> changes);

    /// <summary>Whether a project's run-level report measured this file, including zero-hit lines.</summary>
    bool HasProjectFileCoverage(string projectPath, string filePath);

    /// <summary>
    /// Flags class- and test-level units that executed a changed file as needing re-measurement: once their
    /// code has changed the set of files they execute may differ, so coarser evidence is used for them until
    /// they are measured again.
    /// </summary>
    Task MarkChangedAsync(IReadOnlyList<ChangedFile> changes);

    /// <summary>Returns the units recorded for <paramref name="projectPath"/> at <paramref name="granularity"/>, with whether each is current.</summary>
    IReadOnlyList<ImpactUnitInfo> GetUnits(string projectPath, ImpactGranularity granularity);

    /// <summary>Returns approximate row and text-storage statistics for the impact map.</summary>
    ImpactStoreStats GetStats();

    /// <summary>Forgets the named units of a project, e.g. test classes that no longer exist.</summary>
    Task RemoveUnitsAsync(string projectPath, ImpactGranularity granularity, IReadOnlyCollection<string> unitKeys);

    /// <summary>
    /// Returns each test recorded as executing <paramref name="filePath"/>, judged by the same evidence
    /// <see cref="SelectTests"/> uses: a test with current finer evidence is not credited by a coarser recording.
    /// </summary>
    IReadOnlyList<ImpactExecutor> GetTestsExecuting(string filePath);
}
