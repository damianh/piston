// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Dtos;

/// <summary>Source-input fingerprint captured for a run (<c>piston-source-v3</c>, or <c>piston-source-v2</c> before the solution graph is evaluated).</summary>
public sealed record SourceFingerprintDto(string Algorithm, string Value, int FileCount);
