// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A changed line that ran with only some of its branch outcomes taken.</summary>
public sealed record ChangedLineBranchesDto(int Line, int BranchesCovered, int BranchesTotal);
