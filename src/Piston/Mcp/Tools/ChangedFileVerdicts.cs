// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Coverage;

namespace Piston.Mcp.Tools;

/// <summary>The verdicts for one changed source file.</summary>
/// <param name="FilePath">Absolute path.</param>
/// <param name="IsMeasured">The coverage report has the file.</param>
/// <param name="IsStale">
/// The file changed after it was measured, or a range's version of it differs from the working tree. Lines in its
/// unchanged C# members may still have verdicts from remapped coverage.
/// </param>
/// <param name="IsPartial">Only a selective run measured the file, so tests that did not run may cover more of it.</param>
internal sealed record ChangedFileVerdicts(
    string FilePath, bool IsMeasured, bool IsStale, bool IsPartial, IReadOnlyList<ChangedLineResult> Lines)
{
    public IReadOnlyList<CoveringTest> BranchTests { get; init; } = [];

    public ChangedLineTotals Totals => ChangedLineTotals.Of(Lines);

    /// <summary>The distinct failing tests credited with any of the file's lines, in ordinal order.</summary>
    /// <remarks>Lines credited to the same tests share one list, so each list is read once, not once per line.</remarks>
    public IReadOnlyList<string> FailingTests() =>
        [.. Lines
            .Select(l => l.FailingTests)
            .OfType<IReadOnlyList<string>>()
            .Distinct(ReferenceEqualityComparer.Instance)
            .Cast<IReadOnlyList<string>>()
            .SelectMany(tests => tests)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
