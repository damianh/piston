// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>A test as it was in the baseline run and in the compared run; either side is null when the test did not run there.</summary>
public sealed record RunComparisonEntry(string Project, string Test, StoredTestResultDto? Before, StoredTestResultDto? After)
{
    public double DeltaMs => (After?.DurationMs ?? 0) - (Before?.DurationMs ?? 0);
}
