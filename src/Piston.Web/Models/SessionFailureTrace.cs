// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Web.Models;

/// <summary>Where a currently failing test's failure came from.</summary>
/// <param name="Origin">What the timeline establishes; <paramref name="IntroducedBy"/> is set only for <see cref="FailureOrigin.Run"/>.</param>
/// <param name="IntroducedBy">The run in which the test started failing in this session.</param>
/// <param name="Edit">The edit that produced the source the introducing run tested; null when none is recorded.</param>
/// <param name="SourceUnchanged">No edit is recorded because the introducing run tested the source the session started with.</param>
/// <param name="Classification">How the run history classified the failure, when history is on.</param>
public sealed record SessionFailureTrace(
    TestReference Test,
    FailureOrigin Origin,
    SessionTimelineRun? IntroducedBy,
    SessionTimelineEdit? Edit,
    FailureClassificationDto? Classification,
    bool SourceUnchanged = false);
