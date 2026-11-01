// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A stored run with its per-test results.</summary>
public sealed record StoredRunDto(StoredRunSummaryDto Summary, IReadOnlyList<StoredTestResultDto> Results);
