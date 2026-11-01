// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>A file produced by a test and retained with its run when it fits the artifact limits.</summary>
public sealed record TestAttachment(
    string? Path,
    string? DisplayName = null,
    string? Description = null)
{
    /// <summary>The index used by the dashboard artifact endpoint within <see cref="RunId"/>.</summary>
    public int Index { get; init; }

    /// <summary>The run that owns this attachment.</summary>
    public string? RunId { get; init; }

    /// <summary>The original path reported by the test runner.</summary>
    public string? OriginalPath { get; init; }

    /// <summary>Size in bytes of the source file, when it could be inspected.</summary>
    public long? SizeBytes { get; init; }

    public bool Retained { get; init; }

    /// <summary>Why the file was not copied into Piston's artifact store.</summary>
    public string? NotRetainedReason { get; init; }
}
