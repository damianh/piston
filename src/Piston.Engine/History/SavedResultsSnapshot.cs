// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Engine.Models;

namespace Piston.Engine.History;

internal sealed record SavedResultsSnapshot(
    DateTimeOffset SavedAt,
    DateTimeOffset? LastRunTime,
    TimeSpan? LastBuildDuration,
    TimeSpan? LastTestDuration,
    BuildResult? LastBuild,
    IReadOnlyList<TestSuite> TestSuites,
    TestRunRecord? LastRun);
