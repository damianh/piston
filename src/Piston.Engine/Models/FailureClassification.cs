// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Classification of one failed test in a run, with the evidence it rests on.</summary>
/// <param name="Project">Test project name (suite name).</param>
/// <param name="Test">Fully-qualified name, with the target framework when multi-targeted.</param>
/// <param name="Kind">The classification.</param>
/// <param name="Evidence">One sentence naming the earlier runs it is based on.</param>
/// <param name="Flakiness">The outcomes that make the test flaky; set when <paramref name="Kind"/> is <see cref="FailureKind.Flaky"/>.</param>
public sealed record FailureClassification(string Project, string Test, FailureKind Kind, string Evidence, FlakinessEvidence? Flakiness = null)
{
    public string ProjectId { get; init; } = Project;
}
