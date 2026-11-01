// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Web.Models;

/// <summary>What the watch strip says about a run: what triggered it, its build, its scope and what it changed.</summary>
/// <param name="Trigger">What started the run, e.g. <c>Calc.cs saved</c>.</param>
/// <param name="TriggerDetail">Every changed file, one per line, for the trigger's tooltip; null when not a file change.</param>
/// <param name="Build">The build step, e.g. <c>build 4.2s</c>; null when the run had no build result (yet).</param>
/// <param name="BuildFailed">True when the build failed.</param>
/// <param name="Scope">Which tests ran, e.g. <c>6 tests by changed lines</c>; null when unknown.</param>
/// <param name="Duration">The run's elapsed time; null until it completes.</param>
public sealed record WatchStripSummary(
    string Trigger,
    string? TriggerDetail,
    string? Build,
    bool BuildFailed,
    string? Scope,
    string? Duration);
