// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Impact;

/// <summary>Approximate persisted impact-map size, in UTF-8 bytes.</summary>
internal sealed record ImpactStoreStats(int Units, int FileRows, long SpanBytes, long BranchBytes, long SnapshotBytes)
{
    public long TotalBytes => SpanBytes + BranchBytes + SnapshotBytes;
}
