// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>A test-produced file retained with its run, or metadata explaining why it was not retained.</summary>
public sealed record TestAttachmentDto(
    string? RunId,
    int Index,
    string? DisplayName,
    string? Description,
    string? FileName,
    long? SizeBytes,
    bool Retained,
    string? NotRetainedReason,
    string? Url);
