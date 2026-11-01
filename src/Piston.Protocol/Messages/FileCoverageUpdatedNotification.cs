// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

using Piston.Protocol.Dtos;

namespace Piston.Protocol.Messages;

/// <summary>Pushed after a test run when per-file coverage data is updated.</summary>
public sealed record FileCoverageUpdatedNotification(
    string FilePath,
    IReadOnlyList<CoverageLineDto> Lines
);
