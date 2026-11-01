// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>A run kept in the history with its per-test results.</summary>
public sealed record StoredRun(StoredRunSummary Summary, IReadOnlyList<StoredTestResult> Results);
