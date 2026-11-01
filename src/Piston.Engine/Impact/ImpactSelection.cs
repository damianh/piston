// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

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
