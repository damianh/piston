// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

/// <summary>Parameters for <c>tests/getRunLog</c>: the test project whose latest run log to fetch.</summary>
/// <param name="ProjectId">The solution-relative project ID, as reported in <see cref="Dtos.TestSuiteDto.ProjectId"/>.</param>
public sealed record GetRunLogCommand(string ProjectId);
