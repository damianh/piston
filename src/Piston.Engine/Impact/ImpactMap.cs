// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;

namespace Piston.Engine.Impact;

/// <summary>
/// How precisely an impact-map entry attributes executed code to tests. Higher values are finer.
/// </summary>
internal enum ImpactGranularity
{
    /// <summary>Every test in a project's run is credited with everything the run executed.</summary>
    Run = 0,

    /// <summary>Every test in one class is credited with what that class's tests executed when run alone.</summary>
    Class = 1,

    /// <summary>The test was measured on its own.</summary>
    Test = 2,
}

/// <summary>A source file as a unit's tests executed it.</summary>
/// <param name="FilePath">Absolute path of the source file.</param>
/// <param name="Spans">Executed method line ranges.</param>
/// <param name="Content">The file's content when it was measured; changes are found by diffing against it.</param>
internal sealed record ImpactFile(string FilePath, IReadOnlyList<LineSpan> Spans, string Content);

/// <summary>What one measured unit (a project's run, a test class or a test) executed.</summary>
/// <param name="ProjectPath">The test project the unit belongs to.</param>
/// <param name="Granularity">How the unit was measured.</param>
/// <param name="UnitKey">Identifies the unit within the project: <c>run</c>, a class name or a test name.</param>
/// <param name="RunId">The coverage run that measured it.</param>
/// <param name="TestFqns">The tests credited with the unit's files.</param>
/// <param name="Files">The files executed, with their content when measured.</param>
/// <param name="Merge">
/// True when only some of the unit's tests ran (a selective run): tests and spans are added to what is
/// recorded rather than replacing it. False replaces the unit and marks it current.
/// </param>
/// <param name="MethodsComplete">
/// For a merge: every test case of each method in <paramref name="TestFqns"/> ran (selective runs filter by
/// method). Recorded tests of those methods that did not run no longer exist, such as theory cases whose
/// display names change between runs, and are removed.
/// </param>
internal sealed record ImpactRecording(
    string ProjectPath,
    ImpactGranularity Granularity,
    string UnitKey,
    long RunId,
    IReadOnlyList<string> TestFqns,
    IReadOnlyList<ImpactFile> Files,
    bool Merge,
    bool MethodsComplete = false);

/// <summary>A changed source file to select tests for.</summary>
/// <param name="FilePath">Absolute path of the file.</param>
/// <param name="Content">Its current content, or null when it was deleted.</param>
internal sealed record ChangedFile(string FilePath, string? Content);

/// <summary>Why a test was selected.</summary>
/// <param name="FilePath">The changed file.</param>
/// <param name="ChangedLines">The changed lines, numbered as in the recorded snapshot.</param>
/// <param name="ExecutedSpan">
/// The executed method range the change overlaps, or null when the change lies outside every range the
/// tests executed (or the file was deleted) and every test that executed the file is selected.
/// </param>
/// <param name="Granularity">How precisely the evidence attributes the file to the test.</param>
/// <param name="ProjectPath">The test project.</param>
internal sealed record ImpactReason(
    string FilePath,
    LineSpan ChangedLines,
    LineSpan? ExecutedSpan,
    ImpactGranularity Granularity,
    string ProjectPath);

/// <summary>The tests a set of changes affects, according to the impact map.</summary>
/// <param name="Tests">Each selected test FQN with the reasons it was selected.</param>
/// <param name="UnmappedFiles">
/// Changed files the map has no evidence for; a caller should not narrow a run on the map's say-so when
/// any are present.
/// </param>
internal sealed record ImpactSelection(
    IReadOnlyDictionary<string, IReadOnlyList<ImpactReason>> Tests,
    IReadOnlyList<string> UnmappedFiles)
{
    /// <summary>Mapped files whose source differs only in comments or whitespace.</summary>
    public IReadOnlyList<string> UnchangedFiles { get; init; } = [];
}

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

    /// <summary>
    /// Flags class- and test-level units that executed a changed file as needing re-measurement: once their
    /// code has changed the set of files they execute may differ, so coarser evidence is used for them until
    /// they are measured again.
    /// </summary>
    Task MarkChangedAsync(IReadOnlyList<ChangedFile> changes);

    /// <summary>Returns the units recorded for <paramref name="projectPath"/> at <paramref name="granularity"/>, with whether each is current.</summary>
    IReadOnlyList<ImpactUnitInfo> GetUnits(string projectPath, ImpactGranularity granularity);

    /// <summary>Forgets the named units of a project, e.g. test classes that no longer exist.</summary>
    Task RemoveUnitsAsync(string projectPath, ImpactGranularity granularity, IReadOnlyCollection<string> unitKeys);

    /// <summary>
    /// Returns each test recorded as executing <paramref name="filePath"/>, judged by the same evidence
    /// <see cref="SelectTests"/> uses: a test with current finer evidence is not credited by a coarser recording.
    /// </summary>
    IReadOnlyList<ImpactExecutor> GetTestsExecuting(string filePath);
}

/// <summary>A test the impact map credits with executing a file.</summary>
/// <param name="Spans">The executed method ranges, numbered as in the snapshot the unit was measured against.</param>
/// <param name="SnapshotHash">Hash of the file's content when measured.</param>
/// <param name="IsUnitCurrent">False when the unit's code changed and it awaits re-measurement.</param>
/// <param name="IsContentCurrent">False when the file's current code differs from the measured snapshot.</param>
internal sealed record ImpactExecutor(
    string ProjectPath,
    string TestFqn,
    ImpactGranularity Granularity,
    IReadOnlyList<LineSpan> Spans,
    string SnapshotHash,
    bool IsUnitCurrent,
    bool IsContentCurrent);

/// <summary>A recorded unit's identity and state.</summary>
/// <param name="Tests">The tests credited with the unit's files.</param>
internal sealed record ImpactUnitInfo(
    string UnitKey, long RunId, bool IsCurrent, DateTimeOffset RecordedAt, IReadOnlyList<string> Tests);

internal static class ImpactContent
{
    /// <summary>Hex SHA-256 of the UTF-8 encoding of <paramref name="content"/>.</summary>
    public static string Hash(string content) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    /// <summary>Reads a file for the impact map, or returns null when it cannot be read.</summary>
    public static string? TryRead(string filePath)
    {
        try
        {
            return File.ReadAllText(filePath);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
