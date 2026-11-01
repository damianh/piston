// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A test result key scoped to the project suite that produced it.</summary>
public sealed record TestOutcomeReferenceDto(string Project, string Key)
{
    public string ProjectId { get; init; } = Project;
}
