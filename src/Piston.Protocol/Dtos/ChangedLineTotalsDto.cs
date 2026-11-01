// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Changed lines per verdict.</summary>
public sealed record ChangedLineTotalsDto(
    int ChangedLines, int CoveredByPassing, int CoveredOnlyByFailing, int Uncovered, int NonExecutable, int Unknown);
