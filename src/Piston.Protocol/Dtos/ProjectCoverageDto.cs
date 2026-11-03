// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Latest coverage collection outcome for a test project.</summary>
public sealed record ProjectCoverageDto(string ProjectPath, string Status, string? Message, string? RunId);
