// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A selected run and its comparison baseline.</summary>
public sealed record StoredRunPairDto(StoredRunDto? Run, StoredRunDto? Baseline);
