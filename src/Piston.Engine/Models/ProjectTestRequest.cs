// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Engine.Models;

public sealed record ProjectTestRequest(
    string ProjectPath,
    string? Filter,
    bool CollectCoverage);
