// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Protocol.Messages;

public sealed record BuildCompletedData(
    bool Succeeded,
    double DurationMs,
    int ErrorCount,
    IReadOnlyList<string> Errors
);
