// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

/// <summary>Deterministic content fingerprint of the source inputs a run executed against.</summary>
/// <param name="Algorithm">Fingerprint algorithm identifier: <c>piston-source-v3</c> (project-scoped inputs) or <c>piston-source-v2</c> (every file under the solution directory).</param>
/// <param name="Value">Lower-case hex SHA-256 digest.</param>
/// <param name="FileCount">Number of files included.</param>
public sealed record SourceFingerprint(string Algorithm, string Value, int FileCount);
