// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Execution;

namespace Piston.Engine.Models;

/// <summary>The shared resources of one test project's run and how they were obtained.</summary>
/// <param name="Needs">What the run needed and why.</param>
/// <param name="Wait">The final queue status; null when the run needed no coordination.</param>
/// <param name="Warnings">Problems noticed, e.g. a declared port already bound by a non-Piston process.</param>
public sealed record TestProjectResources(
    TestResourceNeeds Needs,
    TestResourceWait? Wait,
    IReadOnlyList<string> Warnings);
