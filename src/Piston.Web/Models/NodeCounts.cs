// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>Result counts and summed duration for the tests under a tree node.</summary>
public readonly record struct NodeCounts(int Total, int Passed, int Failed, double DurationMs = 0)
{
    /// <summary>A description for screen readers and tooltips, e.g. <c>12 tests: 10 passed, 2 failed</c>.</summary>
    public string Description =>
        $"{Total:N0} {(Total == 1 ? "test" : "tests")}: {Passed:N0} passed, {Failed:N0} failed";

    /// <summary>A description of <see cref="DurationMs"/>, e.g. <c>2.4s total test time</c>.</summary>
    public string DurationDescription => $"{TestNode.FormatDuration(DurationMs)} total test time";
}
