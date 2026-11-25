// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>How a set of changed files was tested by <see cref="IEngine.RunAffectedAsync"/>.</summary>
/// <param name="Plan">What the changes select now.</param>
/// <param name="Run">The run that tested the changes; null when they affect no test.</param>
/// <param name="Reused">
/// True when <paramref name="Run"/> is a run the engine had already made of these changes on the present
/// source, rather than a run started by the request.
/// </param>
public sealed record AffectedRun(ImpactAnalysisResult Plan, TestRunRecord? Run, bool Reused);
