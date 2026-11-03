// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Whether a test project's latest run supplied trustworthy coverage.</summary>
public enum CoverageCollectionStatus
{
    Unknown,
    Measured,
    Partial,
    Unavailable,
    Failed,
}

/// <summary>Coverage collection outcome for one project and the run that produced it.</summary>
public sealed record ProjectCoverageStatus(
    string ProjectPath,
    CoverageCollectionStatus Status,
    string? Message,
    string? RunId);
