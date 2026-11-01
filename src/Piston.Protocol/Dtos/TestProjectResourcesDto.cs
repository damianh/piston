// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>
/// The exclusive resources a test project needed in a run, why, how long it waited, and any resource problems
/// (warnings such as a port already bound outside Piston, or a suspected port/container-name conflict in its output).
/// </summary>
public sealed record TestProjectResourcesDto(
    string ProjectPath,
    IReadOnlyList<string> Keys,
    bool UsesContainers,
    IReadOnlyList<string> Reasons,
    TestResourceWaitDto? Wait,
    IReadOnlyList<string> Warnings,
    string? SuspectedConflict
);
