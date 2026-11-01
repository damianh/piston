// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

// ── Concrete event data records (serialized into ActivityEvent.Data) ──────

public sealed record SolutionLoadedData(
    string SolutionPath,
    int ProjectCount
);
