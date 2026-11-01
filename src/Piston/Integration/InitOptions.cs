// Copyright (c) Damian Hickey. All rights reserved.
// See LICENSE in the project root for license information.

namespace Piston.Integration;

internal sealed record InitOptions(Harness Harness, InitScope Scope, bool Write, bool Force, bool Dashboard);
